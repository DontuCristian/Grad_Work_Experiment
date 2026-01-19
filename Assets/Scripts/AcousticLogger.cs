using System.IO;
using UnityEngine;
using System;
using System.Text;
using UnityEngine.SceneManagement;

public static class AcousticLogger
{
    private static StreamWriter _writer;
    private static StringBuilder _sb = new StringBuilder(1024);
    private static bool _initialized;

    public static void Init( string mode, int rays, string bvhStrategy)
    {
        if (_initialized)
            return;

        string scene = SceneManager.GetActiveScene().name;
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        string fileName =
            $"AcousticLog_" +
            $"scene={scene}_" +
            $"mode={mode}_" +
            $"rays={rays}_" +
            $"bvh={bvhStrategy}_" +
            $"time={timestamp}.csv";

        string path = Path.Combine(Application.persistentDataPath, fileName);

        _writer = new StreamWriter(path, false, Encoding.UTF8);
        _writer.AutoFlush = false;

        // CSV header
        _writer.WriteLine(
            "scene,mode,frame_idx,rays,max_reflections,bvh_strategy," +
            "frame_time_ms,first_reflection_ms,rt60_s"
        );

        Debug.Log($"[AcousticLogger] Logging to {path}");
        _initialized = true;
    }

    public static void LogFrame(
        string mode,
        int frameIdx,
        int rays,
        int maxReflections,
        string bvhStrategy,
        float frameTimeMs,
        float firstReflectionMs,
        float rt60Seconds)
    {
        if (!_initialized)
            return;

        _sb.Clear();

        _sb.Append(SceneManager.GetActiveScene().name).Append(',');
        _sb.Append(mode).Append(',');
        _sb.Append(frameIdx).Append(',');
        _sb.Append(rays).Append(',');
        _sb.Append(maxReflections).Append(',');
        _sb.Append(bvhStrategy).Append(',');
        _sb.Append(frameTimeMs.ToString("F4")).Append(',');
        _sb.Append(firstReflectionMs.ToString("F4")).Append(',');
        _sb.Append(rt60Seconds.ToString("F4"));

        _writer.WriteLine(_sb.ToString());
    }

    public static void Flush()
    {
        _writer?.Flush();
    }

    public static void Shutdown()
    {
        if (!_initialized)
            return;

        _writer.Flush();
        _writer.Close();
        _writer = null;
        _initialized = false;

        Debug.Log("[AcousticLogger] Closed log file");
    }
}