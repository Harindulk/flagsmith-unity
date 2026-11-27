using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Flagsmith
{
    public sealed class HttpRequest
    {
        public string Method;
        public string Url;
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public string Body;
    }

    /// <summary>Status 0 with an Error means the request never reached the server.</summary>
    public sealed class HttpResponse
    {
        public readonly int Status;
        public readonly string Body;
        public readonly string Error;

        public HttpResponse(int status, string body, string error)
        {
            Status = status;
            Body = body;
            Error = error;
        }

        public bool IsSuccess => Status >= 200 && Status < 300;
        public bool IsRetryable => Status == 0 || Status == 408 || Status == 429 || Status >= 500;
    }

    public interface ITransport
    {
        Task<HttpResponse> Send(HttpRequest request);
    }

    /// <summary>UnityWebRequest works on every Unity platform, WebGL included (HttpClient does not).</summary>
    public sealed class UnityWebRequestTransport : ITransport
    {
        public int TimeoutSeconds = 10;

        public Task<HttpResponse> Send(HttpRequest request)
        {
            var tcs = new TaskCompletionSource<HttpResponse>();
            var www = new UnityWebRequest(request.Url, request.Method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = TimeoutSeconds,
            };
            if (request.Body != null)
                www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body)) { contentType = "application/json" };
            foreach (var h in request.Headers) www.SetRequestHeader(h.Key, h.Value);

            www.SendWebRequest().completed += _ =>
            {
                var reached = www.result != UnityWebRequest.Result.ConnectionError;
                tcs.SetResult(new HttpResponse(reached ? (int)www.responseCode : 0,
                    www.downloadHandler?.text, reached ? null : www.error));
                www.Dispose();
            };
            return tcs.Task;
        }
    }
}
