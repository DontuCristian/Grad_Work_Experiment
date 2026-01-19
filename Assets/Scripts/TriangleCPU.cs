using System.Collections.Generic;
using UnityEngine;

public struct TriangleCPU
{
    Vector3 v0;
    Vector3 v1;
    Vector3 v2;

    public Vector3 V0
    {
        get { return v0; }
        set { v0 = value; }
    }

    public Vector3 V1
    {
        get { return v1; }
        set { v1 = value; }
    }
    public Vector3 V2
    {
        get { return v2; }
        set { v2 = value; }
    }

    public static TriangleCPU[] BuildTriangleArray(MeshFilter[] meshes)
    {
        if (meshes == null || meshes.Length == 0)
            return new TriangleCPU[0];

        var triangles = new List<TriangleCPU>();

        foreach (var mf in meshes)
        {
            if (mf == null || mf.sharedMesh == null) continue;

            var mesh = mf.sharedMesh;
            var verts = mesh.vertices;
            var indices = mesh.triangles;

            // This matrix includes position, rotation and scale
            Matrix4x4 localToWorld = mf.transform.localToWorldMatrix;

            for (int i = 0; i < indices.Length; i += 3)
            {
                TriangleCPU t;
                t.v0 = localToWorld.MultiplyPoint3x4(verts[indices[i]]);
                t.v1 = localToWorld.MultiplyPoint3x4(verts[indices[i + 1]]);
                t.v2 = localToWorld.MultiplyPoint3x4(verts[indices[i + 2]]);
                triangles.Add(t);
            }
        }

        return triangles.ToArray();
    }

    public static void ComputeTriangleBounds( TriangleCPU tri, out Vector3 min, out Vector3 max)
    {
        min = Vector3.Min(tri.v0, Vector3.Min(tri.v1, tri.v2));
        max = Vector3.Max(tri.v0, Vector3.Max(tri.v1, tri.v2));
    }

    public static Vector3 TriangleCentroid(TriangleCPU tri)
    {
        return (tri.v0 + tri.v1 + tri.v2) / 3f;
    }
}
