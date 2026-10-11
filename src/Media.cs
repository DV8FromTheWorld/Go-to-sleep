using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;

namespace GoToSleep
{
    // Pauses/resumes anything that shows up in Windows' media controls (Spotify, YouTube in a browser, ...).
    // Runs on a background queue so a slow app can't freeze the lock screen; calls happen in the order they're made.
    static class Media
    {
        static readonly object Gate = new object();
        static Task queue = Task.FromResult(0);
        static List<string> pausedByUs = new List<string>();

        public static void PausePlaying()
        {
            Enqueue(() =>
            {
                var paused = WithSessions(sessions =>
                {
                    var ids = new List<string>();
                    foreach (var s in sessions)
                        if (s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                            Wait(s.TryPauseAsync()))
                            ids.Add(s.SourceAppUserModelId);
                    return ids;
                });
                if (paused.Count > 0) Log.Write("Paused media: " + string.Join(", ", paused));
                pausedByUs = pausedByUs.Union(paused).ToList();
            });
        }

        // Resume only what we paused (and is still paused), then forget it.
        public static void ResumePaused()
        {
            Enqueue(() =>
            {
                var ids = pausedByUs;
                pausedByUs = new List<string>();
                if (ids.Count == 0) return;
                WithSessions(sessions =>
                {
                    foreach (var s in sessions)
                        if (ids.Contains(s.SourceAppUserModelId) &&
                            s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                            Wait(s.TryPlayAsync());
                    return 0;
                });
                Log.Write("Resumed media: " + string.Join(", ", ids));
            });
        }

        // Morning: don't autoplay anything, just drop the list.
        public static void ForgetPaused()
        {
            Enqueue(() => pausedByUs = new List<string>());
        }

        static void Enqueue(Action work)
        {
            lock (Gate)
                queue = queue.ContinueWith(_ =>
                {
                    try { work(); }
                    catch (Exception ex) { Log.Write("Media control failed: " + ex.Message); }
                });
        }

        static T WithSessions<T>(Func<IReadOnlyList<GlobalSystemMediaTransportControlsSession>, T> use)
        {
            var manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            return use(manager.GetSessions());
        }

        // The usual AsTask() helper needs the Windows SDK, so wait on the operation directly (max 3s).
        static T Wait<T>(IAsyncOperation<T> op)
        {
            for (int i = 0; i < 300 && op.Status == AsyncStatus.Started; i++) Thread.Sleep(10);
            if (op.Status != AsyncStatus.Completed) throw new TimeoutException("media call " + op.Status);
            return op.GetResults();
        }
    }
}
