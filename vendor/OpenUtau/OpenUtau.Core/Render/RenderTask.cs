using System;
using System.Threading.Tasks;

namespace OpenUtau.Core.Render {
    /// <summary>
    /// The render pipeline offloads work with Task.Run and then blocks on the result.
    /// Browser wasm has a single thread, so the queued work can never start while the
    /// caller waits for it. Run inline there; everything downstream still sees a Task.
    /// </summary>
    public static class RenderTask {
        public static Task Run(Action action) {
            if (!OperatingSystem.IsBrowser()) {
                return Task.Run(action);
            }
            try {
                action();
                return Task.CompletedTask;
            } catch (Exception e) {
                // Keep faults observable through ContinueWith, as with Task.Run.
                return Task.FromException(e);
            }
        }

        public static Task<T> Run<T>(Func<T> func) {
            if (!OperatingSystem.IsBrowser()) {
                return Task.Run(func);
            }
            try {
                return Task.FromResult(func());
            } catch (Exception e) {
                return Task.FromException<T>(e);
            }
        }
    }
}
