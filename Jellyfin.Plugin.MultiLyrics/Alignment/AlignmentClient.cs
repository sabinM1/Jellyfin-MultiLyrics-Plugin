using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MultiLyrics.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MultiLyrics.Alignment;

/// <summary>
/// Outcome of one alignment attempt.
/// </summary>
/// <param name="Success">True when word-level LRC came back.</param>
/// <param name="Lrc">The aligned LRC content.</param>
/// <param name="Error">Failure reason (safe for logs/status API).</param>
public sealed record AlignmentOutcome(bool Success, string? Lrc = null, string? Error = null);

/// <summary>
/// Client for the optional companion alignment service (Demucs + WhisperX, separate Docker
/// container). Security posture:
/// - URL validated by <see cref="SsrfGuard"/> (metadata endpoints always refused);
/// - redirects disabled at transport level;
/// - every response body capped at Constants.MaxResponseBytes;
/// - request payload capped at Constants.MaxAlignTextChars.
/// Transport: submit + poll by default, synchronous fallback for older stubs.
/// </summary>
public sealed class AlignmentClient
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollBudget = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AlignmentClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlignmentClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public AlignmentClient(IHttpClientFactory httpClientFactory, ILogger<AlignmentClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether an alignment service is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(MultiLyricsPlugin.Config.AlignmentServiceUrl);

    /// <summary>
    /// Aligns plain lyrics against the audio file, producing word-level LRC.
    /// </summary>
    /// <param name="audioPath">Absolute audio path (must be inside the service's music root).</param>
    /// <param name="plainText">Plain lyric text.</param>
    /// <param name="language">Language hint or null.</param>
    /// <param name="separateVocals">Let the service run Demucs vocal isolation first.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<AlignmentOutcome> AlignAsync(
        string audioPath,
        string plainText,
        string? language,
        bool separateVocals,
        CancellationToken cancellationToken)
    {
        var baseUrl = MultiLyricsPlugin.Config.AlignmentServiceUrl;
        var guard = await SsrfGuard.ValidateAsync(baseUrl, MultiLyricsPlugin.Config.AllowPrivateAlignmentHost)
            .ConfigureAwait(false);
        if (!guard.Allowed)
        {
            _logger.LogWarning("Alignment service URL refused by SSRF guard: {Reason}", guard.Reason);
            return new AlignmentOutcome(false, Error: guard.Reason);
        }

        if (string.IsNullOrWhiteSpace(plainText) || plainText.Length > Constants.MaxAlignTextChars)
        {
            return new AlignmentOutcome(false, Error: "text empty or exceeds size cap");
        }

        var client = _httpClientFactory.CreateClient(Constants.HttpClientNames.Alignment);
        var payload = JsonSerializer.Serialize(new AlignRequest(audioPath, plainText, language, separateVocals));

        // Preferred transport: submit + poll (long jobs survive connection hiccups).
        var submit = await PostAsync(client, baseUrl, "align/submit", payload, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        if (submit.Success)
        {
            var jobId = submit.JobId;
            if (!string.IsNullOrEmpty(jobId))
            {
                return await PollAsync(client, baseUrl, jobId, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (submit.StatusCode == 429)
        {
            return new AlignmentOutcome(false, Error: "alignment service busy");
        }
        else if (submit.StatusCode is 404 or 405)
        {
            // Older stub without submit/poll: synchronous call.
            return await PostSyncAsync(client, baseUrl, payload, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("Alignment submit failed with HTTP {StatusCode}", submit.StatusCode);
            return new AlignmentOutcome(false, Error: $"submit failed (HTTP {submit.StatusCode})");
        }

        return new AlignmentOutcome(false, Error: "alignment service returned no job id");
    }

    private async Task<AlignmentOutcome> PollAsync(HttpClient client, string baseUrl, string jobId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + PollBudget;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, CombineUrl(baseUrl, $"align/status/{Uri.EscapeDataString(jobId)}"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if ((int)response.StatusCode == 404)
            {
                return new AlignmentOutcome(false, Error: "job disappeared from service");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Alignment poll returned HTTP {StatusCode}", (int)response.StatusCode);
                continue;
            }

            var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
            var state = TryDeserialize<JobStatus>(body);
            if (state?.State is "done")
            {
                return string.IsNullOrWhiteSpace(state.Lrc)
                    ? new AlignmentOutcome(false, Error: "service returned empty LRC")
                    : new AlignmentOutcome(true, state.Lrc);
            }

            if (state?.State is "failed" or "error")
            {
                return new AlignmentOutcome(false, Error: state.Error ?? "alignment failed");
            }
        }

        return new AlignmentOutcome(false, Error: "alignment timed out");
    }

    private async Task<AlignmentOutcome> PostSyncAsync(HttpClient client, string baseUrl, string payload, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(30));
        var result = await PostAsync(client, baseUrl, "align", payload, Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
        if (!result.Success || result.StatusCode != 200)
        {
            return new AlignmentOutcome(false, Error: result.StatusCode == 429 ? "alignment service busy" : $"align failed (HTTP {result.StatusCode})");
        }

        var body = TryDeserialize<JobStatus>(Encoding.UTF8.GetString(result.Body!));
        if (body is null || string.IsNullOrWhiteSpace(body.Lrc))
        {
            return new AlignmentOutcome(false, Error: body?.Error ?? "service returned empty LRC");
        }

        return new AlignmentOutcome(true, body.Lrc);
    }

    private async Task<(bool Success, int StatusCode, string? JobId, byte[]? Body)> PostAsync(
        HttpClient client,
        string baseUrl,
        string path,
        string jsonPayload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CombineUrl(baseUrl, path))
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                cts.CancelAfter(timeout);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode == 202)
            {
                var status = TryDeserialize<JobStatus>(body);
                return (true, 202, status?.JobId, body);
            }

            if (response.IsSuccessStatusCode)
            {
                return (true, (int)response.StatusCode, null, body);
            }

            return (false, (int)response.StatusCode, null, null);
        }
        catch (HttpRequestException)
        {
            return (false, 0, null, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, 0, null, null);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > Constants.MaxResponseBytes)
            {
                throw new HttpRequestException("response exceeds size cap");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static T? TryDeserialize<T>(byte[]? body)
        where T : class
    {
        if (body is null || body.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? TryDeserialize<T>(string body)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    // Request/response shapes of the companion service (see alignment-service/app.py).
    private sealed record AlignRequest(string AudioPath, string Text, string? Language, bool SeparateVocals);

    private sealed class JobStatus
    {
        public string? JobId { get; set; }

        public string? State { get; set; }

        public string? Lrc { get; set; }

        public string? Error { get; set; }
    }
}
