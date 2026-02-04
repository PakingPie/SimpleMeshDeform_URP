using UnityEngine;

public class GpuBVHSorter
{
    private const int ThreadGroupSize = 256;

    public bool TrySortTriangles(ComputeShader sortShader, Vector3[] worldVertices, int[] triangles, Bounds bounds, out int[] sortedTriangleIndices)
    {
        sortedTriangleIndices = null;

        if (sortShader == null || worldVertices == null || triangles == null)
            return false;

        int triangleCount = triangles.Length / 3;
        if (triangleCount <= 0)
            return false;

        int sortCount = Mathf.NextPowerOfTwo(triangleCount);

        Vector3Int[] triIndices = new Vector3Int[triangleCount];
        for (int i = 0; i < triangleCount; i++)
        {
            triIndices[i] = new Vector3Int(
                triangles[i * 3],
                triangles[i * 3 + 1],
                triangles[i * 3 + 2]
            );
        }

        ComputeBuffer verticesBuffer = null;
        ComputeBuffer trianglesBuffer = null;
        ComputeBuffer codesBuffer = null;
        ComputeBuffer indicesBuffer = null;

        try
        {
            verticesBuffer = new ComputeBuffer(worldVertices.Length, sizeof(float) * 3);
            trianglesBuffer = new ComputeBuffer(triangleCount, sizeof(int) * 3);
            codesBuffer = new ComputeBuffer(sortCount, sizeof(uint));
            indicesBuffer = new ComputeBuffer(sortCount, sizeof(uint));

            verticesBuffer.SetData(worldVertices);
            trianglesBuffer.SetData(triIndices);

            uint[] codesInit = new uint[sortCount];
            uint[] indicesInit = new uint[sortCount];
            for (int i = 0; i < sortCount; i++)
            {
                codesInit[i] = uint.MaxValue;
                indicesInit[i] = uint.MaxValue;
            }
            codesBuffer.SetData(codesInit);
            indicesBuffer.SetData(indicesInit);

            int computeMortonKernel = sortShader.FindKernel("ComputeMorton");
            int bitonicSortKernel = sortShader.FindKernel("BitonicSort");

            sortShader.SetInt("_TriangleCount", triangleCount);
            sortShader.SetVector("_BoundsMin", bounds.min);
            sortShader.SetVector("_BoundsMax", bounds.max);

            sortShader.SetBuffer(computeMortonKernel, "_Vertices", verticesBuffer);
            sortShader.SetBuffer(computeMortonKernel, "_Triangles", trianglesBuffer);
            sortShader.SetBuffer(computeMortonKernel, "_MortonCodes", codesBuffer);
            sortShader.SetBuffer(computeMortonKernel, "_SortedTriangleIndices", indicesBuffer);

            int groupCount = Mathf.CeilToInt(triangleCount / (float)ThreadGroupSize);
            sortShader.Dispatch(computeMortonKernel, groupCount, 1, 1);

            sortShader.SetInt("_SortCount", sortCount);
            sortShader.SetBuffer(bitonicSortKernel, "_MortonCodes", codesBuffer);
            sortShader.SetBuffer(bitonicSortKernel, "_SortedTriangleIndices", indicesBuffer);

            int sortGroupCount = Mathf.CeilToInt(sortCount / (float)ThreadGroupSize);
            for (int k = 2; k <= sortCount; k <<= 1)
            {
                sortShader.SetInt("_SortK", k);
                for (int j = k >> 1; j > 0; j >>= 1)
                {
                    sortShader.SetInt("_SortJ", j);
                    sortShader.Dispatch(bitonicSortKernel, sortGroupCount, 1, 1);
                }
            }

            uint[] sorted = new uint[sortCount];
            indicesBuffer.GetData(sorted);

            sortedTriangleIndices = new int[triangleCount];
            for (int i = 0; i < triangleCount; i++)
            {
                sortedTriangleIndices[i] = (int)sorted[i];
            }

            return true;
        }
        finally
        {
            if (verticesBuffer != null) verticesBuffer.Release();
            if (trianglesBuffer != null) trianglesBuffer.Release();
            if (codesBuffer != null) codesBuffer.Release();
            if (indicesBuffer != null) indicesBuffer.Release();
        }
    }
}
