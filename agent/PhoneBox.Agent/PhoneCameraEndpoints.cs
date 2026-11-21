namespace PhoneBox.Agent;

public static class PhoneCameraEndpoints
{
    public static void MapPhoneCamera(this WebApplication app, int localPort)
    {
        // The LAN allow-list contains only /phone. Receiver, control and OBS output stay on the local settings port.
        app.MapGet("/api/phone-camera/status", (PhoneCamera camera) => Results.Json(camera.Status()));
        app.MapGet("/api/phone-camera/native", (NativeCamera camera) => Results.Json(camera.Status()));
        app.MapPost("/api/phone-camera/native/enable", async (NativeCamera camera) =>
            await camera.Enable(localPort) is { } error ? Results.BadRequest(new { error }) : Results.Ok());
        app.MapPost("/api/phone-camera/native/disable", async (NativeCamera native, PhoneCamera camera) =>
        {
            camera.Stop();
            return await native.Disable() is { } error ? Results.BadRequest(new { error }) : Results.Ok();
        });
        app.MapPost("/api/phone-camera/native/install", async (NativeCamera camera) =>
            await camera.Install(false) is { } error ? Results.BadRequest(new { error }) : Results.Ok());
        app.MapPost("/api/phone-camera/native/uninstall", async (NativeCamera native, PhoneCamera camera) =>
        {
            camera.Stop();
            return await native.Install(true) is { } error ? Results.BadRequest(new { error }) : Results.Ok();
        });
        app.MapPost("/api/phone-camera/request", (string device, PhoneCamera camera) =>
            PhoneRegistry.All().Any(p => p.Id == device && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - p.LastSeen < 15_000)
                && camera.Request(device) ? Results.Ok() : Results.Conflict());
        app.MapPost("/api/phone-camera/stop", (PhoneCamera camera) => { camera.Stop(); return Results.Ok(); });
        app.MapPost("/api/phone-camera/phone", async (HttpContext ctx, PhoneCamera camera) =>
        {
            if (ctx.Connection.LocalPort == localPort) return Results.StatusCode(403);
            var phone = ctx.Request.Headers["X-PhoneBox-Device"].ToString();
            var credential = ctx.Request.Headers.Authorization.ToString();
            var id = ctx.Request.Headers["X-Camera-Request"].ToString();
            var key = ctx.Request.Headers["X-Camera-Session"].ToString();
            switch (ctx.Request.Headers["X-Camera-Action"].ToString())
            {
                case "poll": return Results.Json(camera.Poll(phone));
                case "approve":
                    var session = camera.Approve(phone, id, credential);
                    return session == null ? Results.Conflict() : Results.Json(new { session });
                case "end": return camera.End(phone, id, key, credential) ? Results.Ok() : Results.Conflict();
                case "frame":
                    if (ctx.Request.ContentLength is not (>= 4 and <= PhoneCamera.MaxFrameBytes) ||
                        ctx.Request.ContentType != "image/jpeg" || !int.TryParse(ctx.Request.Headers["X-Camera-Rotation"], out var rotation)) return Results.BadRequest();
                    {
                        // Read at most the declared, bounded length; never buffer an unbounded phone request.
                        var bytes = new byte[(int)ctx.Request.ContentLength.Value];
                        try { await ctx.Request.Body.ReadExactlyAsync(bytes, ctx.RequestAborted); }
                        catch (EndOfStreamException) { return Results.BadRequest(); }
                        return camera.Upload(phone, id, key, credential, bytes, rotation) ? Results.Ok() : Results.Conflict();
                    }
                default: return Results.BadRequest();
            }
        });
        app.MapGet("/api/phone-camera/frame", (PhoneCamera camera, HttpResponse response) =>
        {
            var (jpeg, rotation) = camera.Frame();
            response.Headers["X-Camera-Rotation"] = rotation.ToString();
            return jpeg == null ? Results.NoContent() : Results.Bytes(jpeg, "image/jpeg");
        });
        app.MapGet("/phone-camera", () => Results.Text(Page, "text/html"));
    }

    // OBS Browser Source uses this loopback page. No video or capability is exposed in a URL.
    internal const string Page = """
        <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <title>PhoneBox Camera</title><style>html,body{margin:0;background:#111;color:white;width:100%;height:100%;overflow:hidden}canvas{width:100%;height:100%;object-fit:contain}</style>
        <canvas width="1280" height="720"></canvas><script>
        const canvas=document.querySelector('canvas'),c=canvas.getContext('2d');
        function blank(){c.fillStyle='#111';c.fillRect(0,0,canvas.width,canvas.height);c.fillStyle='#ddd';c.font='32px sans-serif';c.textAlign='center';c.fillText('Camera off',canvas.width/2,canvas.height/2);}
        blank(); let last=0;
        setInterval(()=>{if(performance.now()-last>1500)blank();},200);
        async function update(){
          try{
            const r=await fetch('/api/phone-camera/frame',{cache:'no-store',signal:AbortSignal.timeout(1500)});
            if(r.status!==200){blank();return;}
            const bitmap=await createImageBitmap(await r.blob()),angle=Number(r.headers.get('X-Camera-Rotation'))||0;
            const w=angle%180?bitmap.height:bitmap.width,h=angle%180?bitmap.width:bitmap.height;
            canvas.width=w;canvas.height=h;c.save();c.translate(w/2,h/2);c.rotate(angle*Math.PI/180);c.drawImage(bitmap,-bitmap.width/2,-bitmap.height/2);c.restore();bitmap.close();last=performance.now();
          }catch(_){blank();}finally{setTimeout(update,100);}
        }update();
        </script></html>
        """;
}
