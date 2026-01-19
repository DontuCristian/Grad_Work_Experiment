using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

enum AcousticTestMode
{
    Realtime,
    Reference
}

public class GPUAcousticTest : MonoBehaviour
{
    private bool _isShuttingDown = false;

    [SerializeField] private Transform _parentTranform;
    [SerializeField] private Transform _sourceTransform;
    [SerializeField] private Transform _listenerTransform;
    [SerializeField] private float _listenerRadius = 0.15f;
    [SerializeField] private string _requiredTag = "Acoustic";
    [SerializeField] private bool _includeInactive = false;

    [SerializeField] private AcousticTestMode _mode = AcousticTestMode.Realtime;

    [Header("RealtimeGPUSettings")]
    [SerializeField] private ComputeShader _computeShader;
    [SerializeField] private int _numRays = 10000;
    [SerializeField] private int _maxReflections = 5;

    private float _nextAcousticUpdateTime = 0f;
    private int _frameIndex = 0;

    [Header("ReferenceGPUSettings")]
    [SerializeField] private int _referenceRays = 100000;
    [SerializeField] private int _referenceMaxReflections = 40;
    [SerializeField] private int _referenceMaxIterations = 50;
    [SerializeField] private float _referenceConvergencePct = 0.01f; // 1%

    [Header("GPUSettings")]
    [SerializeField] private int _irBinCount = 1000;
    [SerializeField] private float _irBinSizeMs = 1.0f;

    [Header("AcousticUpdate")]
    [SerializeField] private float _acousticUpdateFreq = 10f;
    [SerializeField] private bool _updateEveryFrame = true;

    [Header("BVH Debugging")]
    [SerializeField] private bool _drawBVH = true;
    [SerializeField] private int _maxBVHDepthToDraw = 100;
    [SerializeField] private int _debugLeafIndex = 0;
    [SerializeField] int _bvhTestBatches = 100;
    [SerializeField] int _bvhRaysPerBatch = 1000;

    private MeshFilter[] _meshFilters;
    private TriangleCPU[] _triangles;

    private ComputeBuffer _triangleBuffer;
    private ComputeBuffer _triangleIndexBuffer;
    private ComputeBuffer _irBuffer;
    private ComputeBuffer _bvhBuffer;
    private ComputeBuffer _debugBuffer;

    private float _frameStartTime;
    private float _lastDispatchTimeMs;
    private float _firstReflectionMs = -1f;
    private float _rt60Seconds = -1f;


    uint[] _irClearArray;
    private float[] _lastIrFloat;
    private int _kernelHandle;

    private Stopwatch _sw;

    // Reference
    private float[] _referenceIrAccum;
    private float _referenceRt60Prev = -1f;

    private float _referenceRt60Final = -1f;

    // Coroutine
    private Coroutine _activeCoroutine;

    // BVH
    private BVHNode[] _bvhNodes;
    private BVHBuilder _bvhBuilder = new BVHBuilder();

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        _irClearArray = new uint[_irBinCount]; // defaults to all zeros
        _lastIrFloat = new float[_irBinCount];

        // -------- Collect MeshFilters and build triangle array --------
        BuildTriangleArray();

        // -------- Build BVH --------
        _bvhNodes = _bvhBuilder.Build(_triangles);

        UnityEngine.Debug.Log($"BVH built: {_bvhNodes.Length} nodes for {_triangles.Length} triangles");

        // -------- Initialize GPU resources --------
        GPUInit();

        // -------- Set the mode --------
        switch (_mode)
        {
            case AcousticTestMode.Reference:
                StartReference();
                AcousticLogger.Init(mode: "reference", rays: _referenceRays, bvhStrategy: "none");
                break;
            case AcousticTestMode.Realtime:
                StartRealtime();
                AcousticLogger.Init(mode: "realtime", rays: _numRays, bvhStrategy: "refit");
                break;
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (_triangleBuffer == null || _computeShader == null)
            return;

        if (_sourceTransform == null)
        {
            UnityEngine.Debug.LogWarning("Source transform not assigned.");
            return;
        }
    }

    void LateUpdate()
    {
        if (_sourceTransform.hasChanged || _listenerTransform.hasChanged)
        {
            _sourceTransform.hasChanged = false;
            _listenerTransform.hasChanged = false;
        }
    }

    private void ProcessIR(ReadOnlySpan<uint> irRaw, float[] scratchIR, bool accumulate, float[] accumulationTarget, out float firstReflectionMs, out float rt60Seconds)
    {
        const float ENERGY_SCALE = 1_000_000f;

        // Always clear scratch
        Array.Clear(scratchIR, 0, scratchIR.Length);

        int count = Mathf.Min(irRaw.Length, scratchIR.Length);

        for (int i = 0; i < count; i++)
        {
            float val = irRaw[i] / ENERGY_SCALE;
            scratchIR[i] = val;

            if (accumulate)
                accumulationTarget[i] += val / _numRays;
        }

        float[] analysisIR = accumulate ? accumulationTarget : scratchIR;

        firstReflectionMs = IRAnalyzer.ComputeFirstReflectionMs(analysisIR, _irBinSizeMs);

        rt60Seconds = IRAnalyzer.ComputeRT60(analysisIR, _irBinSizeMs);
    }

    void GPUInit()
    {
        if (_computeShader == null)
        {
            UnityEngine.Debug.LogError("ComputeShader is not assigned.");
            return;
        }

        _kernelHandle = _computeShader.FindKernel("CSMain");

        //Triangle buffer (3 float for 3 vectors, 9 floats in total)
        _triangleBuffer = new ComputeBuffer(_triangles.Length, sizeof(float) * 9);
        _triangleBuffer.SetData(_triangles);


        _irBuffer = new ComputeBuffer(_irBinCount, sizeof(uint), ComputeBufferType.Raw);

        _bvhBuffer = new ComputeBuffer(_bvhNodes.Length, sizeof(float) * 6 + sizeof(int) * 4);
        _bvhBuffer.SetData(_bvhNodes);

        _debugBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.Raw);


        _triangleIndexBuffer = new ComputeBuffer(_bvhBuilder._triangleIndices.Length, sizeof(int));
        _triangleIndexBuffer.SetData(_bvhBuilder._triangleIndices);

        uint[] zero = new uint[4];
        _debugBuffer.SetData(zero);

        //Bind buffers and constants to compute shader
        _computeShader.SetFloat("_SpeedOfSound", 343.0f);
        _computeShader.SetInt("_IRBinCount", _irBinCount);
        _computeShader.SetFloat("_BinSizeMs", _irBinSizeMs);
        _computeShader.SetInt("_BVHNodeCount", _bvhNodes.Length);

        _computeShader.SetBuffer(_kernelHandle, "_Triangles", _triangleBuffer);
        _computeShader.SetBuffer(_kernelHandle, "_IRBins", _irBuffer);
        _computeShader.SetBuffer(_kernelHandle, "_BVHNodes", _bvhBuffer);
        _computeShader.SetBuffer(_kernelHandle, "_DebugCounters", _debugBuffer);
        _computeShader.SetBuffer(_kernelHandle, "_TriangleIndices", _triangleIndexBuffer);
    }

    void BuildTriangleArray()
    {
        if (_parentTranform == null)
        {
            UnityEngine.Debug.LogWarning(nameof(_parentTranform) + " is null. No meshes collected.");
            _meshFilters = new MeshFilter[0];
            return;
        }

        // If _requiredTag is empty, keep all MeshFilters; otherwise filter by tag.
        if (string.IsNullOrEmpty(_requiredTag))
        {
            _meshFilters = _parentTranform.GetComponentsInChildren<MeshFilter>(_includeInactive);
        }
        else
        {
            _meshFilters = _parentTranform
                .GetComponentsInChildren<MeshFilter>(_includeInactive)
                .Where(mf => mf != null && mf.gameObject != null && mf.gameObject.CompareTag(_requiredTag))
                .ToArray();
        }

        UnityEngine.Debug.Log($"Collected {_meshFilters.Length} MeshFilters with tag '{_requiredTag}'.");

        _triangles = TriangleCPU.BuildTriangleArray(_meshFilters);
        UnityEngine.Debug.Log($"Built {_triangles.Length} world-space triangles from geometry.");


        if (_triangles.Length == 0)
        {
            UnityEngine.Debug.LogWarning("No triangles found. Compute shader will not be initialized.");
            return;
        }
    }
    void UpdateWorldSpaceTriangles()
    {
        int triIdx = 0;

        foreach (var mf in _meshFilters)
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;

            var verts = mesh.vertices;
            var tris = mesh.triangles;
            var l2w = mf.transform.localToWorldMatrix;

            for (int i = 0; i < tris.Length; i += 3)
            {
                _triangles[triIdx].V0 = l2w.MultiplyPoint3x4(verts[tris[i]]);
                _triangles[triIdx].V1 = l2w.MultiplyPoint3x4(verts[tris[i + 1]]);
                _triangles[triIdx].V2 = l2w.MultiplyPoint3x4(verts[tris[i + 2]]);
                triIdx++;
            }
        }
    }
    void OnDestroy()
    {
        _isShuttingDown = true;
        StopActiveCoroutine();

        // Release compute buffers
        _triangleBuffer?.Release();
        _irBuffer?.Release();
        _bvhBuffer?.Release();
        _triangleIndexBuffer?.Release();
        _debugBuffer?.Release();

        AcousticLogger.Shutdown();
    }

    // Coroutines for realtime and reference capture
    IEnumerator ReferenceCoroutine()
    {
        _mode = AcousticTestMode.Reference;

        _computeShader.SetInt("_NumRays", _referenceRays);
        _computeShader.SetInt("_MaxBounces", _referenceMaxReflections);

        _referenceIrAccum = new float[_irBinCount];
        Array.Clear(_referenceIrAccum, 0, _referenceIrAccum.Length);

        _referenceRt60Prev = -1f;

        for (int iteration = 0; iteration < _referenceMaxIterations; iteration++)
        {
            _irBuffer.SetData(_irClearArray);
            _computeShader.SetInt("_FrameIndex", iteration);

            int groups = Mathf.CeilToInt(_referenceRays / 64f);
            _computeShader.Dispatch(_kernelHandle, groups, 1, 1);

            var request = AsyncGPUReadback.Request(_irBuffer);
            yield return new WaitUntil(() => request.done); // IMPORTANT

            if (request.hasError)
            {
                UnityEngine.Debug.LogError("GPU readback error in reference run");
                yield break;
            }

            // --- Convert snapshot ---
            float[] snapshot = new float[_irBinCount];
            ProcessIR(
                request.GetData<uint>(),
                snapshot,
                accumulate: false,
                accumulationTarget: null,
                out float firstRefMs,
                out float rt60
            );

            // --- Accumulate ---
            for (int i = 0; i < _irBinCount; i++)
                _referenceIrAccum[i] += snapshot[i];

            // --- Normalize ---
            for (int i = 0; i < _irBinCount; i++)
                _referenceIrAccum[i] /= (iteration + 1);

            float currentRT60 = IRAnalyzer.ComputeRT60(_referenceIrAccum, _irBinSizeMs);

            //if (_referenceRt60Prev > 0f &&
            //    Mathf.Abs(currentRT60 - _referenceRt60Prev) / _referenceRt60Prev < _referenceConvergencePct)
            //{
            //    UnityEngine.Debug.Log($"Reference converged at iteration {iteration}");
            //    break;
            //}

            _referenceRt60Prev = currentRT60;
            yield return null;
        }

        _referenceRt60Final = _referenceRt60Prev;

        AcousticLogger.LogFrame(
            mode: "reference",
            frameIdx: 0,
            rays: _referenceRays,
            maxReflections: _referenceMaxReflections,
            bvhStrategy: "none",
            frameTimeMs: float.NaN,
            firstReflectionMs: IRAnalyzer.ComputeFirstReflectionMs(_referenceIrAccum, _irBinSizeMs),
            rt60Seconds: _referenceRt60Final
        );

        UnityEngine.Debug.Log("Reference run completed correctly");
        Application.Quit();
    }
    IEnumerator RealtimeCoroutine()
    {
        _mode = AcousticTestMode.Realtime;

        _computeShader.SetInt("_NumRays", _numRays);
        _computeShader.SetInt("_MaxBounces", _maxReflections);
        _computeShader.SetInt("_BVHNodeCount", _bvhNodes.Length);

        Array.Clear(_lastIrFloat, 0, _lastIrFloat.Length);

        _computeShader.SetBuffer(_kernelHandle, "_Triangles", _triangleBuffer);
        _computeShader.SetBuffer(_kernelHandle, "_BVHNodes", _bvhBuffer);

        _nextAcousticUpdateTime = Time.time;

        float interval = 1f / Mathf.Max(_acousticUpdateFreq, 1f);

        while (_mode == AcousticTestMode.Realtime)
        {
            _frameIndex++;

            // --- Slow acoustic update ---
            if (Time.time >= _nextAcousticUpdateTime)
            {
                UpdateAcousticState();
                _nextAcousticUpdateTime = Time.time + (1f / _acousticUpdateFreq);
            }

            // --- Fast per-frame ray dispatch ---
            _computeShader.SetInt("_FrameIndex", Time.frameCount);

            int groups = Mathf.CeilToInt(_numRays / 64f);

            _frameStartTime = Time.realtimeSinceStartup;
            _computeShader.Dispatch(_kernelHandle, groups, 1, 1);
            var request = AsyncGPUReadback.Request(_irBuffer, OnRealtimeReadback);

            if (!_updateEveryFrame)
                yield return new WaitForSeconds(interval);
            else
                yield return null;

            if (_frameIndex > 300)
            {
                Application.Quit();
                yield break;
            }
        }
    }

    void OnRealtimeReadback(AsyncGPUReadbackRequest request)
    {
        if (_isShuttingDown || _mode != AcousticTestMode.Realtime || request.hasError)
            return;

        var rawArr = request.GetData<uint>().ToArray();

        ProcessIR(
            request.GetData<uint>().ToArray(),
            _lastIrFloat,
            accumulate: false,
            accumulationTarget: new float[_irBinCount],
            out _firstReflectionMs,
            out _rt60Seconds);

        float frameEndTime = Time.realtimeSinceStartup;
        _lastDispatchTimeMs = (frameEndTime - _frameStartTime) * 1000f;

        AcousticLogger.LogFrame(
            mode: "realtime",
            frameIdx: Time.frameCount,
            rays: _numRays,
            maxReflections: _maxReflections,
            bvhStrategy: "refit",
            frameTimeMs: _lastDispatchTimeMs,
            firstReflectionMs: _firstReflectionMs,
            rt60Seconds: _rt60Seconds);
    }

    void UpdateAcousticState()
    {
        UpdateWorldSpaceTriangles();

        _bvhBuilder.RecomputeTriangleBounds();
        _bvhBuilder.RefitBVH(_bvhNodes);

        // Upload updated data to GPU
        _triangleBuffer.SetData(_triangles);
        _bvhBuffer.SetData(_bvhNodes);
        _irBuffer.SetData(_irClearArray);
        _triangleIndexBuffer.SetData(_bvhBuilder._triangleIndices);

        _computeShader.SetInt("_NumTriangles", _triangles.Length);
        _computeShader.SetVector("_SourcePos", _sourceTransform.position);
        _computeShader.SetVector("_ListenerPos", _listenerTransform.position);
        _computeShader.SetFloat("_ListenerRadius", _listenerRadius);
        _computeShader.SetInt("_BVHNodeCount", _bvhNodes.Length);
    }

    void StartRealtime()
    {
        StopActiveCoroutine();
        _activeCoroutine = StartCoroutine(RealtimeCoroutine());
    }

    void StartReference()
    {
        StopActiveCoroutine();
        _activeCoroutine = StartCoroutine(ReferenceCoroutine());
    }

    void StopActiveCoroutine()
    {
        if (_activeCoroutine != null)
        {
            StopCoroutine(_activeCoroutine);
            _activeCoroutine = null;
        }
    }

    private void OnApplicationQuit()
    {
        _isShuttingDown = true;
        AcousticLogger.Shutdown();
    }

    IEnumerator RunBVHValidation()
    {
        UnityEngine.Debug.Log("[BVH TEST] Starting validation…");
        int totalTests = _bvhTestBatches * _bvhRaysPerBatch;
        int errors = 0;

        for (int batch = 0; batch < _bvhTestBatches; batch++)
        {
            int seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue); UnityEngine.Random.InitState(seed); for (int i = 0; i < _bvhRaysPerBatch; i++)
            {
                Vector3 origin = _sourceTransform.position;
                Vector3 dir = UnityEngine.Random.onUnitSphere;
                bool bruteHit = _bvhBuilder.BruteForceIntersect(origin, dir, out float bruteT);
                bool bvhHit = _bvhBuilder.IntersectRayBVH(_bvhNodes, origin, dir, out float bvhT);

                if (bruteHit != bvhHit || (bruteHit && Mathf.Abs(bruteT - bvhT) > 1e-3f))
                {
                    errors++;
                    UnityEngine.Debug.LogError($"[BVH TEST] Mismatch! seed={seed}, batch={batch}, ray={i}, " + $"bruteHit={bruteHit}, bvhHit={bvhHit}, " + $"bruteT={bruteT}, bvhT={bvhT}");
                    yield break; // stop immediately
                }
            }
            // Yield once per batch so the editor doesn't freeze
            yield return null;
        }
        UnityEngine.Debug.Log($"[BVH TEST] Passed {totalTests} rays tested, 0 errors");
    }
    void DrawBVHNode(int nodeIndex, int depth)
    {
        if (nodeIndex < 0 || nodeIndex >= _bvhNodes.Length)
            return;

        BVHNode node = _bvhNodes[nodeIndex]; // Color by depth
        float t = depth / (float)_maxBVHDepthToDraw;
        Gizmos.color = Color.Lerp(Color.green, Color.red, t);
        Vector3 center = (node.boundsMin + node.boundsMax) * 0.5f;
        Vector3 size = node.boundsMax - node.boundsMin;
        if (node.triangleCount > 0) // leaf
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(center, size);
            return;
        }
        //Recurse
        if (node.leftChild >= 0)
            DrawBVHNode(node.leftChild, depth + 1);
        if (node.rightChild >= 0)
            DrawBVHNode(node.rightChild, depth + 1);
    }
    void OnDrawGizmos()
    {
        if (!_drawBVH || _bvhNodes == null || _bvhNodes.Length == 0) return;
        DrawBVHNode(0, 0);
    }
}
    

