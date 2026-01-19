using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct BVHNode
{
    public Vector3 boundsMin;
    public Vector3 boundsMax;

    public int leftChild;      // -1 if leaf
    public int rightChild;     // -1 if leaf

    public int firstTriangle;  // index into triangle array
    public int triangleCount;  // >0 => leaf
}

public class BVHBuilder
{
    const int MAXLEAFTRIANGLES = 4;

    private TriangleCPU[] _triangles;
    public int[] _triangleIndices;

    private Vector3[] _triMin;
    private Vector3[] _triMax;
    private Vector3[] _triCentroid;

    private List<BVHNode> _nodes;

    // Entry point
    public BVHNode[] Build(TriangleCPU[] inputTriangles)
    {
        _triangles = inputTriangles;
        int triCount = _triangles.Length;

        _triangleIndices = Enumerable.Range(0, triCount).ToArray();
        _nodes = new List<BVHNode>(triCount * 2);

        // Cache triangle data ONCE
        _triMin = new Vector3[triCount];
        _triMax = new Vector3[triCount];
        _triCentroid = new Vector3[triCount];

        for (int i = 0; i < triCount; i++)
        {
            TriangleCPU.ComputeTriangleBounds(_triangles[i], out _triMin[i], out _triMax[i]);
            _triCentroid[i] = (_triMin[i] + _triMax[i]) * 0.5f;
        }

        BuildNode(0, triCount);
        return _nodes.ToArray();
    }

    private int BuildNode(int start, int count)
    {
        // Compute bounds for this node
        Vector3 boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (int i = start; i < start + count; i++)
        {
            int triIndex = _triangleIndices[i];
            boundsMin = Vector3.Min(boundsMin, _triMin[triIndex]);
            boundsMax = Vector3.Max(boundsMax, _triMax[triIndex]);
        }

        int nodeIndex = _nodes.Count;
        _nodes.Add(new BVHNode()); // placeholder

        // Create leaf
        if (count <= MAXLEAFTRIANGLES)
        {
            _nodes[nodeIndex] = new BVHNode
            {
                boundsMin = boundsMin,
                boundsMax = boundsMax,
                leftChild = -1,
                rightChild = -1,
                firstTriangle = start,
                triangleCount = count
            };
            return nodeIndex;
        }

        // Choose split axis (largest extent)
        Vector3 extent = boundsMax - boundsMin;
        int axis =
            (extent.x > extent.y && extent.x > extent.z) ? 0 :
            (extent.y > extent.z) ? 1 : 2;

        // Sort triangle indices by centroid
        Array.Sort(
            _triangleIndices,
            start,
            count,
            Comparer<int>.Create(
                (a, b) => _triCentroid[a][axis].CompareTo(_triCentroid[b][axis])
            )
        );

        int mid = start + count / 2;

        // Build children
        int leftChild = BuildNode(start, mid - start);
        int rightChild = BuildNode(mid, start + count - mid);

        // Write internal node
        _nodes[nodeIndex] = new BVHNode
        {
            boundsMin = boundsMin,
            boundsMax = boundsMax,
            leftChild = leftChild,
            rightChild = rightChild,
            firstTriangle = -1,
            triangleCount = 0
        };

        return nodeIndex;
    }

    public void RecomputeTriangleBounds()
    {
        for (int i = 0; i < _triangles.Length; i++)
        {
            TriangleCPU.ComputeTriangleBounds(
                _triangles[i],
                out _triMin[i],
                out _triMax[i]
            );

            _triCentroid[i] = (_triMin[i] + _triMax[i]) * 0.5f;
        }
    }
    public void RefitBVH(BVHNode[] nodes)
    {
        RefitNode(0, nodes);
    }

    private void RefitNode(int nodeIndex, BVHNode[] nodes)
    {
        BVHNode node = nodes[nodeIndex];

        // Leaf: compute bounds from triangles
        if (node.triangleCount > 0)
        {
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < node.triangleCount; i++)
            {
                int triIndex = _triangleIndices[node.firstTriangle + i];
                min = Vector3.Min(min, _triMin[triIndex]);
                max = Vector3.Max(max, _triMax[triIndex]);
            }

            node.boundsMin = min;
            node.boundsMax = max;
            nodes[nodeIndex] = node;
            return;
        }

        // Internal: refit children first
        RefitNode(node.leftChild, nodes);
        RefitNode(node.rightChild, nodes);

        BVHNode left = nodes[node.leftChild];
        BVHNode right = nodes[node.rightChild];

        node.boundsMin = Vector3.Min(left.boundsMin, right.boundsMin);
        node.boundsMax = Vector3.Max(left.boundsMax, right.boundsMax);
        nodes[nodeIndex] = node;
    }

    // BVH Traversal test
    static bool RayAABB(Vector3 origin, Vector3 invDir, Vector3 min, Vector3 max, float tMax)
    {
        float t1 = (min.x - origin.x) * invDir.x;
        float t2 = (max.x - origin.x) * invDir.x;
        float tmin = Mathf.Min(t1, t2);
        float tmax = Mathf.Max(t1, t2);

        t1 = (min.y - origin.y) * invDir.y;
        t2 = (max.y - origin.y) * invDir.y;
        tmin = Mathf.Max(tmin, Mathf.Min(t1, t2));
        tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));

        t1 = (min.z - origin.z) * invDir.z;
        t2 = (max.z - origin.z) * invDir.z;
        tmin = Mathf.Max(tmin, Mathf.Min(t1, t2));
        tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));

        return tmax >= Mathf.Max(0f, tmin) && tmin <= tMax;
    }

    static bool RayTriangleIntersect(Vector3 orig, Vector3 dir, TriangleCPU tri, out float t)
    {
        const float EPS = 1e-5f;
        Vector3 v0v1 = tri.V1 - tri.V0;
        Vector3 v0v2 = tri.V2 - tri.V0;
        Vector3 pvec = Vector3.Cross(dir, v0v2);
        float det = Vector3.Dot(v0v1, pvec);

        if (Mathf.Abs(det) < EPS)
        {
            t = 0;
            return false;
        }


        float invDet = 1.0f / det;
        Vector3 tvec = orig - tri.V0;
        float u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0.0 || u > 1.0)
        {
            t = 0; 
            return false;
        }

        Vector3 qvec = Vector3.Cross(tvec, v0v1);
        float v = Vector3.Dot(qvec, dir) * invDet;
        if (v < 0.0 || u + v > 1.0)
        {
            t = 0;
            return false;
        }

        t = Vector3.Dot(v0v2, qvec) * invDet;
        return t > EPS;
    }

    public bool IntersectRayBVH(BVHNode[] bvhNodes, Vector3 origin, Vector3 dir, out float hitDistance)
    {
        hitDistance = float.MaxValue;

        const float EPS = 1e-8f;
        Vector3 invDir = new Vector3(
            1f / (Mathf.Abs(dir.x) > EPS ? dir.x : Mathf.Sign(dir.x) * EPS),
            1f / (Mathf.Abs(dir.y) > EPS ? dir.y : Mathf.Sign(dir.y) * EPS),
            1f / (Mathf.Abs(dir.z) > EPS ? dir.z : Mathf.Sign(dir.z) * EPS)
        );

        Stack<int> stack = new Stack<int>();
        stack.Push(0); // root node

        bool hit = false;

        while (stack.Count > 0)
        {
            int nodeIndex = stack.Pop();
            BVHNode node = bvhNodes[nodeIndex];

            if (!RayAABB(origin, invDir, node.boundsMin, node.boundsMax, hitDistance))
                continue;

            if (node.triangleCount > 0)
            {
                // Leaf
                for (int i = 0; i < node.triangleCount; i++)
                {
                    int triIndex = _triangleIndices[node.firstTriangle + i];
                    if (RayTriangleIntersect(origin, dir, _triangles[triIndex], out float t) && t < hitDistance)
                    {
                        hitDistance = t;
                        hit = true;
                    }
                }
            }
            else
            {
                // Internal node
                stack.Push(node.rightChild);
                stack.Push(node.leftChild);
            }
        }

        return hit;
    }

    public bool BruteForceIntersect(Vector3 origin, Vector3 dir, out float hitDistance)
    {
        hitDistance = float.MaxValue;
        bool hit = false;

        for (int i = 0; i < _triangles.Length; i++)
        {
            if (RayTriangleIntersect(origin, dir, _triangles[i], out float t))
            {
                if (t < hitDistance)
                {
                    hitDistance = t;
                    hit = true;
                }
            }
        }

        return hit;
    }
}