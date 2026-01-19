using UnityEngine;

public static class RunNotifier
{
    public static void NotifyRunFinished()
    {
#if UNITY_STANDALONE_WINDOWS
        WindowsNotifier.Notify("Acoustic Experiment", "Run finished successfully");
#elif UNITY_STANDALONE_OSX
        MacNotifier.Notify("Acoustic Experiment", "Run finished successfully");
#elif UNITY_ANDROID
        AndroidNotifier.Notify("Acoustic Experiment", "Run finished");
#else
        Debug.Log("Run finished (no native notifier on this platform)");
#endif
    }
}
