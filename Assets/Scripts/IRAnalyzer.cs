using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class IRAnalyzer
{
    /// <summary>
    /// Computes first reflection time in milliseconds using a threshold
    /// equal to 1% of the peak IR energy.
    /// </summary>
    public static float ComputeFirstReflectionMs(float[] ir, float binSizeMs, int consecutiveBins = 4)
    {
        if (ir == null || ir.Length == 0)
            return -1f;

        int ignoreBins = Mathf.CeilToInt(5f / binSizeMs); // ignore direct sound
        if (ignoreBins >= ir.Length)
            return -1f;

        float peak = ir.Skip(ignoreBins).Max();
        if (peak <= 0f)
            return -1f;

        float threshold = Mathf.Max(peak * 0.01f, 1e-6f);

        int count = 0;
        for (int i = ignoreBins; i < ir.Length; i++)
        {
            if (ir[i] >= threshold)
            {
                count++;
                if (count >= consecutiveBins)
                    return (i - consecutiveBins + 1) * binSizeMs;
            }
            else
            {
                count = 0;
            }
        }

        return -1f;
    }


    /// <summary>
    /// Estimates RT60 in seconds using Schroeder integration
    /// and linear regression between -5 dB and -35 dB.
    /// </summary>
    public static float ComputeRT60(float[] ir, float binSizeMs) 
    {
        int n = ir.Length;
        if (n == 0)
            return -1f;

        // --- Schroeder integration ---
        float[] energy = new float[n];
        float cumulative = 0f;

        for (int i = n - 1; i >= 0; i--)
        {
            cumulative += ir[i];
            energy[i] = cumulative;
        }

        if (energy[0] <= 0f)
            return -1f;

        // --- Convert to dB ---
        float[] energyDb = new float[n];
        for (int i = 0; i < n; i++)
        {
            const float EPS = 1e-20f;
            energyDb[i] = 10f * MathF.Log10(MathF.Max(energy[i] / energy[0], EPS));
        }

        for (int i = 1; i < n; i++)
        {
            energyDb[i] = MathF.Min(energyDb[i], energyDb[i - 1]);
        }

        // --- Select regression range (-5 dB to -35 dB) ---
        List<float> times = new List<float>();
        List<float> dbs = new List<float>();

        for (int i = 0; i < n; i++)
        {
            float db = energyDb[i];
            if (db <= -5f && db >= -35f)
            {
                times.Add(i * binSizeMs * 0.001f); // ms -> seconds
                dbs.Add(db);
            }
        }

        if (times.Count < 2)
            return -1f;

        // --- Linear regression (dB vs time) ---
        float sumT = 0f, sumDb = 0f, sumTDb = 0f, sumTT = 0f;
        int m = times.Count;

        for (int i = 0; i < m; i++)
        {
            sumT += times[i];
            sumDb += dbs[i];
            sumTDb += times[i] * dbs[i];
            sumTT += times[i] * times[i];
        }

        float denom = m * sumTT - sumT * sumT;
        if (Math.Abs(denom) < 1e-6f)
            return -1f;

        float slope = (m * sumTDb - sumT * sumDb) / denom;

        if (slope >= 0f)
            return -1f;

        // --- Extrapolate to -60 dB ---
        float rt60 = (-60f) / slope; // RT30 extrapolated to RT60
        return rt60;
    }
}
