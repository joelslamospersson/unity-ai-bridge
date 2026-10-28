using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAIBridge
{
    /// <summary>Opt-in image generation. API billing consent precedes the request; asset import is staged separately.</summary>
    public static class ImageGenerationService
    {
        static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        const int MaximumBytes = 8 * 1024 * 1024;

        sealed class Options
        {
            public string Prompt, Path, Model, Url, Key, Size, Quality, Background;
        }

        static T Scoped<T>(bool allowWrite, Func<T> action)
        {
            int access = BridgeSettings.Current.AccessLevel;
            try
            {
                if (allowWrite && !Permissions.CanPropose) BridgeSettings.Current.AccessLevel = BridgeSettings.AccessAsk;
                return action();
            }
            finally { BridgeSettings.Current.AccessLevel = access; }
        }

        static Options Validate(JsonValue args, bool allowWrite)
        {
            return Scoped(allowWrite, () =>
            {
                var settings = BridgeSettings.Current;
                if (!settings.ImageGenerationEnabled) throw new BridgeException("Enable image generation in the Generate Image window first. It uses OpenAI API billing, separately from ChatGPT plan usage.");
                if (string.IsNullOrWhiteSpace(BridgeSettings.Secrets.OpenAiApiKey)) throw new BridgeException("Image generation requires an OpenAI API key saved in Account & settings.");
                string prompt = args["prompt"].AsString("").Trim();
                if (prompt.Length == 0 || prompt.Length > 32000) throw new BridgeException("Image prompt must contain 1–32000 characters.");
                string path = args["path"].AsString(""), full;
                string error = Permissions.CheckWrite(path, out full, false, true);
                if (error != null) throw new BridgeException(error);
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("Generated image destination must end with .png.");
                if (File.Exists(full) || File.Exists(full + ".meta")) throw new BridgeException("Generated image destination already exists; choose a new path.");
                string size = args["size"].AsString("1024x1024"), quality = args["quality"].AsString("auto"), background = args["background"].AsString("transparent");
                if (size != "1024x1024" && size != "1536x1024" && size != "1024x1536") throw new BridgeException("Choose 1024x1024, 1536x1024 or 1024x1536.");
                if (quality != "auto" && quality != "low" && quality != "medium" && quality != "high") throw new BridgeException("Image quality must be auto, low, medium or high.");
                if (background != "transparent" && background != "opaque" && background != "auto") throw new BridgeException("Image background must be transparent, opaque or auto.");
                if (string.IsNullOrWhiteSpace(settings.ImageGenerationModel)) throw new BridgeException("Select an image-generation model in the Generate Image window.");
                return new Options { Prompt = prompt, Path = BridgePaths.ToProjectRelative(full), Model = settings.ImageGenerationModel,
                    Url = settings.OpenAiBaseUrl.TrimEnd('/') + "/images/generations", Key = BridgeSettings.Secrets.OpenAiApiKey,
                    Size = size, Quality = quality, Background = background };
            });
        }

        static string BillingFingerprint(Options options)
        {
            string state = JsonValue.Obj("model", options.Model, "url", options.Url, "key", options.Key, "prompt", options.Prompt,
                "path", options.Path, "size", options.Size, "quality", options.Quality, "background", options.Background).ToJson(false);
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(state)));
        }

        internal static async Task<JsonValue> GenerateAndStageAsync(JsonValue args, CancellationToken token,
            bool allowWrite = false, bool costConfirmedInUi = false, ToolCall call = null, ChatApprovalPrompt restored = null)
        {
            if (!await Gate.WaitAsync(0, token).ConfigureAwait(false)) throw new BridgeException("Another image generation is already in progress.");
            try
            {
                token.ThrowIfCancellationRequested();
                if (restored != null && restored.ExternalRequestStarted)
                    throw new BridgeException("Image generation was interrupted after submission. The provider may have processed and billed it. Review pending image imports and provider usage before explicitly requesting another generation; no automatic retry was sent.");
                var options = MainThread.Invoke(() => Validate(args, allowWrite));
                if (!costConfirmedInUi)
                {
                    var prompt = restored ?? new ChatApprovalPrompt
                    {
                        Kind = "image-generation", Title = "Generate one image with OpenAI API billing?",
                        Tool = call ?? new ToolCall { Id = "image_" + Guid.NewGuid().ToString("N"), Name = "generate_image", Arguments = args.ToJson(false) },
                        RequireExplicitConsent = true, ScopedWrite = allowWrite,
                        BillingFingerprint = BillingFingerprint(options),
                        Detail = "Model: " + options.Model + "\nSize: " + options.Size + "\nQuality: " + options.Quality +
                            "\nBackground: " + options.Background + "\nDestination: " + options.Path +
                            "\n\nPrompt:\n" + FileService.RedactSecrets(options.Prompt) +
                            "\n\nThis uses the saved OpenAI API key and incurs API charges. It does not use ChatGPT plan image credits. No automatic generation retry is performed."
                    };
                    var decision = await ChatApproval.RequestAsync(prompt, token).ConfigureAwait(false);
                    if (decision.Resolution != "Accept")
                        return Envelope(JsonValue.Obj("status", decision.Resolution == "Comment" ? "RevisionRequested" : "Denied", "message", "Image generation was not run; no API request was sent.", "user_comment", decision.Comment ?? ""));
                    // Settings/permissions/destination may have changed while consent was pending.
                    var current = MainThread.Invoke(() => Validate(args, allowWrite));
                    if (prompt.BillingFingerprint != BillingFingerprint(current))
                        throw new BridgeException("Image-generation options changed while awaiting consent. Review the updated request before generating.");
                    ChatApproval.MarkExternalRequestStarted(decision.Id);
                }
                token.ThrowIfCancellationRequested();
                var body = JsonValue.Obj("model", options.Model, "prompt", options.Prompt, "n", 1, "size", options.Size,
                    "quality", options.Quality, "background", options.Background, "output_format", "png");
                var reply = await RequestAsync(options, body, token).ConfigureAwait(false);
                string encoded = reply["data"][0]["b64_json"].AsString("");
                if (encoded.Length == 0 || encoded.Length > MaximumBytes * 4 / 3 + 8) throw new BridgeException("The image API returned no PNG or exceeded the 8 MB image limit.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(encoded); }
                catch (FormatException) { throw new BridgeException("The image API returned invalid image data."); }
                ValidatePng(bytes);
                token.ThrowIfCancellationRequested();
                return MainThread.Invoke(() => Scoped(allowWrite, () =>
                {
                    token.ThrowIfCancellationRequested();
                    BridgePaths.EnsureDirectory(BridgePaths.IncomingDir);
                    string incoming = Path.Combine(BridgePaths.IncomingDir, Guid.NewGuid().ToString("N") + ".png");
                    try
                    {
                        using (var file = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes, 0, bytes.Length);
                        var parameters = JsonValue.Obj("incoming_path", incoming, "create_only", true, "sprite_mode", "Single", "pixels_per_unit", 100,
                            "alpha_is_transparency", true, "mipmaps", false, "generation_model", options.Model,
                            "generation_size", options.Size, "generation_background", options.Background);
                        var change = ChangeService.Stage(ChangeKinds.ImportSprite, "Import generated image " + Path.GetFileName(options.Path), options.Path, null, parameters, "image-generation", false);
                        return Envelope(JsonValue.Obj("id", change.Id, "status", change.Status, "path", change.TargetPath, "preview_path", incoming,
                            "message", "Image generated. Import is " + change.Status + "; verify the change status before claiming it is applied.", "usage", reply["usage"]));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        if (!File.Exists(incoming)) throw;
                        return Envelope(JsonValue.Obj("status", "GeneratedNeedsImport", "preview_path", incoming,
                            "message", "The image was generated and retained for preview, but import could not be staged. Choose a new destination and import this existing preview without another paid generation. " + FileService.RedactSecrets(e.Message),
                            "usage", reply["usage"]), true);
                    }
                }));
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpStatusException e) { throw new BridgeException(FileService.RedactSecrets(e.Message)); }
            finally { Gate.Release(); }
        }

        static async Task<JsonValue> RequestAsync(Options options, JsonValue body, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Post, options.Url))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + options.Key);
                request.Content = new StringContent(body.ToJson(false), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    using (timeout.Token.Register(() => { try { response.Dispose(); } catch { } }))
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192];
                        int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                        {
                            if (output.Length + count > 16 * 1024 * 1024) throw new BridgeException("The image API response exceeded the 16 MB response limit.");
                            output.Write(buffer, 0, count);
                        }
                        timeout.Token.ThrowIfCancellationRequested();
                        string text = Encoding.UTF8.GetString(output.ToArray());
                        if (!response.IsSuccessStatusCode)
                        {
                            string safeBody = text.Replace(options.Key, "***REDACTED***");
                            throw new HttpStatusException((int)response.StatusCode, StreamingHttp.DescribeError(response.StatusCode, safeBody), safeBody);
                        }
                        JsonValue value; string error;
                        if (!JsonValue.TryParse(text, out value, out error)) throw new BridgeException("The image API returned invalid JSON.");
                        return value;
                    }
                }
                catch (Exception) when (timeout.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    throw new BridgeException("Image generation exceeded ten minutes. The provider may still have processed the request. No automatic retry was sent.");
                }
            }
        }

        static void ValidatePng(byte[] bytes)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length < 33 || bytes.Length > MaximumBytes) throw new BridgeException("Generated PNG is empty, truncated or too large.");
            for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) throw new BridgeException("The image API did not return a PNG.");
            if (Encoding.ASCII.GetString(bytes, 12, 4) != "IHDR") throw new BridgeException("Generated PNG has no valid image header.");
            long width = 0, height = 0;
            for (int i = 0; i < 4; i++) { width = width * 256 + bytes[16 + i]; height = height * 256 + bytes[20 + i]; }
            if (width < 1 || height < 1 || width > 4096 || height > 4096) throw new BridgeException("Generated image dimensions exceed the supported bounds.");
        }

        static JsonValue Envelope(JsonValue result, bool error = false)
        {
            return JsonValue.Obj("isError", error, "structuredContent", result, "content", JsonValue.Arr(JsonValue.Obj("type", "text", "text", result.ToJson(false))));
        }
    }
}
