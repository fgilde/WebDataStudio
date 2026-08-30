using System.Net;
using System.Text.Json;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// The page the rich file viewer runs on.
///
/// It is its own document on purpose. The component is a Blazor application behind a custom
/// element: it puts its stylesheets — MudBlazor, Roboto and five more — into whatever document
/// loads it, which repaints the studio white, and its WebAssembly runtime refuses to start in a
/// `srcdoc` frame at all ("the URI 'about:srcdoc' is not contained by the base URI"). A real URL
/// in a frame gives it a document of its own to redecorate, and takes the runtime away again when
/// the frame closes.
public static class ViewerEndpoints
{
    public static void MapViewerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/viewer/frame", (string url, string? name, string? type, bool? dark,
            FileViewerOptions options, HttpContext ctx) =>
        {
            if (!options.Enabled)
                return Results.NotFound(new { message = "this studio has no file viewer" });

            // Only what this studio itself serves. The page loads a script and hands it a URL, so a
            // link to it must not be a way to frame somebody else's site or to make the studio
            // fetch an address a visitor chose.
            if (!IsOurs(url))
                return Results.BadRequest(new
                {
                    message = "the viewer only opens files this studio serves",
                });

            var settings = JsonSerializer.Serialize(new
            {
                url,
                name = name ?? "",
                contentType = type ?? "",
                dark = dark ?? false,
            });

            // No caching: the settings are in the query string and the file behind them is not.
            ctx.Response.Headers.CacheControl = "no-store";

            return Results.Content(Page(options.ScriptUrl!, settings), "text/html");
        }).AllowAnonymous();
    }

    /// A path on this studio, or a blob the browser made for itself. Anything absolute is refused:
    /// `//host/x` and `https://host/x` both leave, and both are somebody else's page.
    public static bool IsOurs(string url) =>
        url is { Length: > 0 }
        && (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)));

    private static string Page(string script, string settings) =>
        $$"""
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <title>File</title>
          <style>
            html, body { margin: 0; height: 100%; overflow: hidden; }
            #host { height: 100%; }
          </style>
        </head>
        <body>
          <div id="host"></div>
          <script>
            var settings = {{settings}};
            var told = false;

            function tell(what, detail) {
              if (told) return;
              told = what === "failed";
              // Any origin: the message says "ready" or what went wrong and carries nothing
              // private, and the window that reads it only listens to this frame. Naming an
              // origin here would break a studio reached through a proxy under another name.
              parent.postMessage({ mudex: what, detail: detail || "" }, "*");
            }

            window.addEventListener("error", function (e) { tell("failed", e.message); });
            window.addEventListener("unhandledrejection", function (e) {
              tell("failed", String((e.reason && e.reason.message) || e.reason));
            });

            var script = document.createElement("script");
            script.src = {{JsonSerializer.Serialize(script)}};
            script.onerror = function () { tell("failed", "the viewer could not be fetched"); };

            script.onload = function () {
              customElements.whenDefined("mudex-file-display").then(function () {
                var element = document.createElement("mudex-file-display");

                element.setAttribute("url", settings.url);
                element.setAttribute("file-name", settings.name);
                if (settings.contentType) element.setAttribute("content-type", settings.contentType);
                // The window around this one carries the name already.
                element.setAttribute("show-file-name", "false");
                element.setAttribute("dense", "true");
                // As an attribute, never as a property: this element has a `style` of its own, a
                // string, and setting `style.display` on it throws.
                element.setAttribute("style", "display:block;width:100%;height:100%");

                document.getElementById("host").appendChild(element);

                if (settings.dark && window.MudEx && window.MudEx.setDarkMode) {
                  try { window.MudEx.setDarkMode(true); } catch (e) { /* light it is */ }
                }

                tell("ready");
              });
            };

            document.head.appendChild(script);
          </script>
        </body>
        </html>
        """;
}
