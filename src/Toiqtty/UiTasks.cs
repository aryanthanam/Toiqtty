namespace Toiqtty;

/// <summary>Runs UI-triggered asynchronous work. A failure is logged and reported,
/// never rethrown on the dispatcher where it would end the window and every session.</summary>
internal static class UiTasks
{
    /// <summary>Raised on the faulting continuation's thread (normally the UI thread).</summary>
    internal static event Action<Exception>? Faulted;

    internal static async void Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            try { File.AppendAllText(Path.Combine(Models.SessionStore.Folder, "ui-errors.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            try { Faulted?.Invoke(ex); }
            catch (Exception reportError) { System.Diagnostics.Debug.WriteLine(reportError); }
        }
    }

    internal static void Observe(Func<Task> work)
    {
        Task task;
        try { task = work(); }
        catch (Exception ex) { task = Task.FromException(ex); }
        Observe(task);
    }
}
