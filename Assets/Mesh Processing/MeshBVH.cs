using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// AABB Bounding Volume Hierarchy for mesh intersection queries.
/// Used to constrain vertices to the original mesh surface.
/// </summary>
public class MeshBVH
{
    public class BVHNode
    {
        public Bounds bounds;
        public BVHNode left;
        public BVHNode right;
        public int[] triangleIndices;
        public bool IsLeaf => triangleIndices != null;
    }

    private BVHNode root;
    private Vector3[] worldVertices;
    private int[] triangles;
    private int maxTrianglesPerLeaf;
    private int maxDepth;

    public BVHNode Root => root;
    public Vector3[] WorldVertices => worldVertices;
    public int[] Triangles => triangles;

    /// <summary>
    /// Builds the BVH from pre-allocated vertex/triangle arrays.
    /// This overload avoids redundant Mesh property access (which allocates).
    /// </summary>
    public void Build(Vector3[] localVertices, int[] triangleIndices, Transform transform,
                      int maxTrianglesPerLeaf = 4, int maxDepth = 25)
    {
        this.maxTrianglesPerLeaf = maxTrianglesPerLeaf;
        this.maxDepth = maxDepth;
        this.triangles = triangleIndices;

        worldVertices = new Vector3[localVertices.Length];
        for (int i = 0; i < localVertices.Length; i++)
        {
            worldVertices[i] = transform.TransformPoint(localVertices[i]);
        }

        int triangleCount = triangles.Length / 3;
        List<int> allTriangles = new List<int>(triangleCount);
        for (int i = 0; i < triangleCount; i++)
        {
            allTriangles.Add(i);
        }

        root = BuildNode(allTriangles, 0);

        Debug.Log($"MeshBVH built: {triangleCount} triangles, depth up to {maxDepth}");
    }

    /// <summary>
    /// Convenience overload that reads arrays from the Mesh object.
    /// Note: accessing Mesh.vertices and Mesh.triangles allocates new arrays.
    /// Prefer the (Vector3[], int[], Transform) overload when arrays are already cached.
    /// </summary>
    public void Build(Mesh mesh, Transform transform, int maxTrianglesPerLeaf = 4, int maxDepth = 25)
    {
        Build(mesh.vertices, mesh.triangles, transform, maxTrianglesPerLeaf, maxDepth);
    }

    private BVHNode BuildNode(List<int> triangleIndices, int depth)
    {
        BVHNode node = new BVHNode();
        node.bounds = CalculateBounds(triangleIndices);

        if (triangleIndices.Count <= maxTrianglesPerLeaf || depth >= maxDepth)
        {
            node.triangleIndices = triangleIndices.ToArray();
            return node;
        }

        Vector3 size = node.bounds.size;
        int axis;
        if (size.x >= size.y && size.x >= size.z) axis = 0;
        else if (size.y >= size.x && size.y >= size.z) axis = 1;
        else axis = 2;

        triangleIndices.Sort((a, b) =>
        {
            Vector3 centroidA = GetTriangleCentroid(a);
            Vector3 centroidB = GetTriangleCentroid(b);
            return centroidA[axis].CompareTo(centroidB[axis]);
        });

        int mid = triangleIndices.Count / 2;
        if (mid == 0) mid = 1;

        List<int> leftTriangles = triangleIndices.GetRange(0, mid);
        List<int> rightTriangles = triangleIndices.GetRange(mid, triangleIndices.Count - mid);

        node.left = BuildNode(leftTriangles, depth + 1);
        node.right = BuildNode(rightTriangles, depth + 1);

        return node;
    }

    private Bounds CalculateBounds(List<int> triangleIndices)
    {
        if (triangleIndices.Count == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        foreach (int triIdx in triangleIndices)
        {
            int i0 = triangles[triIdx * 3];
            int i1 = triangles[triIdx * 3 + 1];
            int i2 = triangles[triIdx * 3 + 2];

            min = Vector3.Min(min, worldVertices[i0]);
            min = Vector3.Min(min, worldVertices[i1]);
            min = Vector3.Min(min, worldVertices[i2]);

            max = Vector3.Max(max, worldVertices[i0]);
            max = Vector3.Max(max, worldVertices[i1]);
            max = Vector3.Max(max, worldVertices[i2]);
        }

        Bounds bounds = new Bounds();
        bounds.SetMinMax(min, max);
        return bounds;
    }

    private Vector3 GetTriangleCentroid(int triIdx)
    {
        int i0 = triangles[triIdx * 3];
        int i1 = triangles[triIdx * 3 + 1];
        int i2 = triangles[triIdx * 3 + 2];
        return (worldVertices[i0] + worldVertices[i1] + worldVertices[i2]) / 3f;
    }

    public bool Raycast(Vector3 origin, Vector3 direction, out Vector3 hitPoint, out float hitDistance, float maxDistance = float.MaxValue)
    {
        hitPoint = Vector3.zero;
        hitDistance = maxDistance;

        if (root == null) return false;

        return RaycastNode(root, origin, direction, ref hitPoint, ref hitDistance);
    }

    private bool RaycastNode(BVHNode node, Vector3 origin, Vector3 direction, ref Vector3 hitPoint, ref float hitDistance)
    {
        if (!RayIntersectsBounds(origin, direction, node.bounds, hitDistance))
            return false;

        if (node.IsLeaf)
        {
            bool anyHit = false;
            foreach (int triIdx in node.triangleIndices)
            {
                if (RayTriangleIntersection(origin, direction, triIdx, out Vector3 point, out float dist))
                {
                    if (dist > 0.0001f && dist < hitDistance)
                    {
                        hitDistance = dist;
                        hitPoint = point;
                        anyHit = true;
                    }
                }
            }
            return anyHit;
        }

        bool hitLeft = RaycastNode(node.left, origin, direction, ref hitPoint, ref hitDistance);
        bool hitRight = RaycastNode(node.right, origin, direction, ref hitPoint, ref hitDistance);

        return hitLeft || hitRight;
    }

    private bool RayIntersectsBounds(Vector3 origin, Vector3 direction, Bounds bounds, float maxDist)
    {
        Vector3 invDir = new Vector3(
            Mathf.Abs(direction.x) > 0.0001f ? 1f / direction.x : (direction.x >= 0 ? float.MaxValue : float.MinValue),
            Mathf.Abs(direction.y) > 0.0001f ? 1f / direction.y : (direction.y >= 0 ? float.MaxValue : float.MinValue),
            Mathf.Abs(direction.z) > 0.0001f ? 1f / direction.z : (direction.z >= 0 ? float.MaxValue : float.MinValue)
        );

        float t1x = (bounds.min.x - origin.x) * invDir.x;
        float t2x = (bounds.max.x - origin.x) * invDir.x;
        float t1y = (bounds.min.y - origin.y) * invDir.y;
        float t2y = (bounds.max.y - origin.y) * invDir.y;
        float t1z = (bounds.min.z - origin.z) * invDir.z;
        float t2z = (bounds.max.z - origin.z) * invDir.z;

        float tmin = Mathf.Max(Mathf.Max(Mathf.Min(t1x, t2x), Mathf.Min(t1y, t2y)), Mathf.Min(t1z, t2z));
        float tmax = Mathf.Min(Mathf.Min(Mathf.Max(t1x, t2x), Mathf.Max(t1y, t2y)), Mathf.Max(t1z, t2z));

        return tmax >= 0 && tmin <= tmax && tmin <= maxDist;
    }

    private bool RayTriangleIntersection(Vector3 origin, Vector3 direction, int triIdx, out Vector3 hitPoint, out float distance)
    {
        hitPoint = Vector3.zero;
        distance = 0;

        int i0 = triangles[triIdx * 3];
        int i1 = triangles[triIdx * 3 + 1];
        int i2 = triangles[triIdx * 3 + 2];

        Vector3 v0 = worldVertices[i0];
        Vector3 v1 = worldVertices[i1];
        Vector3 v2 = worldVertices[i2];

        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 h = Vector3.Cross(direction, edge2);
        float a = Vector3.Dot(edge1, h);

        if (Mathf.Abs(a) < 0.0001f)
            return false;

        float f = 1f / a;
        Vector3 s = origin - v0;
        float u = f * Vector3.Dot(s, h);

        if (u < 0f || u > 1f)
            return false;

        Vector3 q = Vector3.Cross(s, edge1);
        float v = f * Vector3.Dot(direction, q);

        if (v < 0f || u + v > 1f)
            return false;

        float t = f * Vector3.Dot(edge2, q);

        if (t > 0.0001f)
        {
            distance = t;
            hitPoint = origin + direction * t;
            return true;
        }

        return false;
    }

    public Vector3 ClosestPointOnMesh(Vector3 point, out float distance)
    {
        distance = float.MaxValue;
        Vector3 closestPoint = point;

        if (root == null) return point;

        ClosestPointOnNode(root, point, ref closestPoint, ref distance);
        return closestPoint;
    }

    private void ClosestPointOnNode(BVHNode node, Vector3 point, ref Vector3 closestPoint, ref float closestDistance)
    {
        float boundsDist = DistanceToBounds(point, node.bounds);
        if (boundsDist >= closestDistance)
            return;

        if (node.IsLeaf)
        {
            foreach (int triIdx in node.triangleIndices)
            {
                Vector3 triClosest = ClosestPointOnTriangle(point, triIdx);
                float dist = Vector3.Distance(point, triClosest);
                if (dist < closestDistance)
                {
                    closestDistance = dist;
                    closestPoint = triClosest;
                }
            }
            return;
        }

        float leftDist = DistanceToBounds(point, node.left.bounds);
        float rightDist = DistanceToBounds(point, node.right.bounds);

        if (leftDist < rightDist)
        {
            ClosestPointOnNode(node.left, point, ref closestPoint, ref closestDistance);
            ClosestPointOnNode(node.right, point, ref closestPoint, ref closestDistance);
        }
        else
        {
            ClosestPointOnNode(node.right, point, ref closestPoint, ref closestDistance);
            ClosestPointOnNode(node.left, point, ref closestPoint, ref closestDistance);
        }
    }

    private float DistanceToBounds(Vector3 point, Bounds bounds)
    {
        Vector3 closest = bounds.ClosestPoint(point);
        return Vector3.Distance(point, closest);
    }

    private Vector3 ClosestPointOnTriangle(Vector3 point, int triIdx)
    {
        int i0 = triangles[triIdx * 3];
        int i1 = triangles[triIdx * 3 + 1];
        int i2 = triangles[triIdx * 3 + 2];

        Vector3 a = worldVertices[i0];
        Vector3 b = worldVertices[i1];
        Vector3 c = worldVertices[i2];

        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = point - a;

        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = point - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3);
            return a + v * ab;
        }

        Vector3 cp = point - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6);
            return a + w * ac;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return b + w * (c - b);
        }

        float denom = 1f / (va + vb + vc);
        float v2 = vb * denom;
        float w2 = vc * denom;
        return a + ab * v2 + ac * w2;
    }
}