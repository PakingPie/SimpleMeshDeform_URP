using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Main controller for the mesh grinding and drilling system.
/// Supports multi-directional grinding and mesh-accurate bounds via BVH.
/// </summary>
public class MeshGrinder : MonoBehaviour
{
    [Header("Tool References")]
    [SerializeField] private GrindTool grindTool;
    [SerializeField] private DrillTool drillTool;
    [SerializeField] private List<GrindableObject> grindableObjects = new List<GrindableObject>();

    [Header("Operation Mode")]
    [SerializeField] private OperationMode currentMode = OperationMode.Grinding;

    [Header("Compute Shader (Optional)")]
    [SerializeField] private ComputeShader grindingComputeShader;
    [SerializeField] private ComputeShader drillingComputeShader;
    [SerializeField] private bool useComputeShader = true;
    [SerializeField] private int computeShaderThreshold = 1000;

    [Header("Processing Settings")]
    [Tooltip("Minimum time between operations in seconds")]
    [SerializeField] private float operationInterval = 0.016f;

    [Tooltip("Auto-weld after each operation")]
    [SerializeField] private bool autoWeld = false;

    [Tooltip("Weld when operation stops")]
    [SerializeField] private bool weldOnComplete = true;

    [Header("Input Actions")]
    [SerializeField] private InputAction toggleOperationAction;
    [SerializeField] private InputAction resetMeshAction;
    [SerializeField] private InputAction weldVerticesAction;
    [SerializeField] private InputAction switchModeAction;

    [Header("Status")]
    [SerializeField] private bool isOperating = false;

    public enum OperationMode
    {
        Grinding,
        Drilling
    }

    private float lastOperationTime;
    private ComputeBuffer vertexBuffer;
    private ComputeBuffer resultBuffer;
    private ComputeBuffer bvhNodeBuffer;
    private ComputeBuffer bvhTriangleBuffer;
    private int grindKernelHandle;
    private int drillKernelHandle;
    private bool grindComputeInitialized = false;
    private bool drillComputeInitialized = false;

    private const int THREAD_GROUP_SIZE = 256;

    public OperationMode CurrentMode
    {
        get => currentMode;
        set => currentMode = value;
    }

    public bool IsOperating => isOperating;

    private void Awake()
    {
        SetupInputActions();
    }

    private void OnEnable()
    {
        EnableInputActions();
    }

    private void OnDisable()
    {
        DisableInputActions();
    }

    private void Start()
    {
        InitializeComputeShaders();
    }

    private void SetupInputActions()
    {
        if (toggleOperationAction == null || toggleOperationAction.bindings.Count == 0)
        {
            toggleOperationAction = new InputAction("ToggleOperation", binding: "<Keyboard>/space");
        }

        if (resetMeshAction == null || resetMeshAction.bindings.Count == 0)
        {
            resetMeshAction = new InputAction("ResetMesh", binding: "<Keyboard>/r");
        }

        if (weldVerticesAction == null || weldVerticesAction.bindings.Count == 0)
        {
            weldVerticesAction = new InputAction("WeldVertices", binding: "<Keyboard>/w");
        }

        if (switchModeAction == null || switchModeAction.bindings.Count == 0)
        {
            switchModeAction = new InputAction("SwitchMode", binding: "<Keyboard>/tab");
        }

        toggleOperationAction.performed += OnToggleOperation;
        resetMeshAction.performed += OnResetMesh;
        weldVerticesAction.performed += OnWeldVertices;
        switchModeAction.performed += OnSwitchMode;
    }

    private void EnableInputActions()
    {
        toggleOperationAction?.Enable();
        resetMeshAction?.Enable();
        weldVerticesAction?.Enable();
        switchModeAction?.Enable();
    }

    private void DisableInputActions()
    {
        toggleOperationAction?.Disable();
        resetMeshAction?.Disable();
        weldVerticesAction?.Disable();
        switchModeAction?.Disable();
    }

    private void OnToggleOperation(InputAction.CallbackContext context) => ToggleOperation();
    private void OnResetMesh(InputAction.CallbackContext context) => ResetAllMeshes();
    private void OnWeldVertices(InputAction.CallbackContext context) => WeldAllVertices();
    private void OnSwitchMode(InputAction.CallbackContext context) => SwitchMode();

    private void Update()
    {
        if (isOperating && UnityEngine.Time.time - lastOperationTime >= operationInterval)
        {
            PerformOperation();
            lastOperationTime = UnityEngine.Time.time;
        }
    }

    public void ToggleOperation()
    {
        isOperating = !isOperating;
        Debug.Log($"{currentMode}: {(isOperating ? "Started" : "Stopped")}");

        if (!isOperating && weldOnComplete)
        {
            WeldAllVertices();
        }
    }

    public void StartOperation()
    {
        isOperating = true;
        Debug.Log($"{currentMode} started");
    }

    public void StopOperation()
    {
        isOperating = false;
        Debug.Log($"{currentMode} stopped");

        if (weldOnComplete)
        {
            WeldAllVertices();
        }
    }

    public void SwitchMode()
    {
        bool wasOperating = isOperating;
        if (wasOperating)
        {
            StopOperation();
        }

        currentMode = currentMode == OperationMode.Grinding
            ? OperationMode.Drilling
            : OperationMode.Grinding;

        Debug.Log($"Switched to {currentMode} mode");
    }

    public void PerformOperation()
    {
        switch (currentMode)
        {
            case OperationMode.Grinding:
                PerformGrinding();
                break;
            case OperationMode.Drilling:
                PerformDrilling();
                break;
        }
    }

    public void PerformGrinding()
    {
        if (grindTool == null)
        {
            Debug.LogWarning("No GrindTool assigned!");
            return;
        }

        foreach (var grindable in grindableObjects)
        {
            if (grindable == null) continue;

            int affected;
            if (useComputeShader && grindComputeInitialized && grindable.VertexCount > computeShaderThreshold)
            {
                affected = PerformGrindingGPU(grindable);
            }
            else
            {
                affected = grindable.ApplyGrinding(grindTool);
            }

            if (autoWeld && affected > 0)
            {
                grindable.WeldVertices();
            }
        }
    }

    public void PerformDrilling()
    {
        if (drillTool == null)
        {
            Debug.LogWarning("No DrillTool assigned!");
            return;
        }

        foreach (var grindable in grindableObjects)
        {
            if (grindable == null) continue;

            int affected;
            if (useComputeShader && drillComputeInitialized && grindable.VertexCount > computeShaderThreshold)
            {
                affected = PerformDrillingGPU(grindable);
            }
            else
            {
                affected = grindable.ApplyDrilling(drillTool);
            }

            if (autoWeld && affected > 0)
            {
                grindable.WeldVertices();
            }
        }
    }

    private void InitializeComputeShaders()
    {
        if (grindingComputeShader != null)
        {
            try
            {
                grindKernelHandle = grindingComputeShader.FindKernel("CSMain");
                grindComputeInitialized = true;
                Debug.Log("Grinding compute shader initialized");
            }
            catch (System.Exception e)
            {
                grindComputeInitialized = false;
                Debug.LogError($"Failed to initialize grinding compute shader: {e.Message}");
            }
        }

        if (drillingComputeShader != null)
        {
            try
            {
                drillKernelHandle = drillingComputeShader.FindKernel("CSMain");
                drillComputeInitialized = true;
                Debug.Log("Drilling compute shader initialized");
            }
            catch (System.Exception e)
            {
                drillComputeInitialized = false;
                Debug.LogError($"Failed to initialize drilling compute shader: {e.Message}");
            }
        }
    }

    private int PerformGrindingGPU(GrindableObject grindable)
    {
        if (!grindComputeInitialized)
        {
            Debug.LogWarning("Compute shader not initialized, falling back to CPU");
            return grindable.ApplyGrinding(grindTool);
        }

        Vector3[] vertices = grindable.GetVertices();
        int vertexCount = vertices.Length;
        if (vertexCount == 0) return 0;

        Vector4[] vertexData = new Vector4[vertexCount];
        Matrix4x4 localToWorld = grindable.transform.localToWorldMatrix;
        Matrix4x4 worldToLocal = grindable.transform.worldToLocalMatrix;

        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 worldPos = localToWorld.MultiplyPoint3x4(vertices[i]);
            vertexData[i] = new Vector4(worldPos.x, worldPos.y, worldPos.z, 0);
        }

        ReleaseBuffers();
        vertexBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 4);
        resultBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 4);
        vertexBuffer.SetData(vertexData);

        // Set shader parameters
        grindingComputeShader.SetBuffer(grindKernelHandle, "vertices", vertexBuffer);
        grindingComputeShader.SetBuffer(grindKernelHandle, "results", resultBuffer);
        grindingComputeShader.SetInt("vertexCount", vertexCount);

        // Tool transform for multi-directional grinding
        grindingComputeShader.SetMatrix("toolWorldToLocal", grindTool.transform.worldToLocalMatrix);
        grindingComputeShader.SetMatrix("toolLocalToWorld", grindTool.transform.localToWorldMatrix);

        // Grind axis info
        var (axisIndex, axisSign) = grindTool.GetLocalAxisInfo();
        grindingComputeShader.SetInt("grindAxisIndex", axisIndex);
        grindingComputeShader.SetFloat("grindAxisSign", axisSign);

        // Custom direction option
        grindingComputeShader.SetInt("useCustomDirection", grindTool.UseCustomDirection ? 1 : 0);
        Vector3 customDir = grindTool.CustomDirection;
        Vector3 grindDir = grindTool.GrindDirection;
        grindingComputeShader.SetVector("customDirection", new Vector4(customDir.x, customDir.y, customDir.z, 0));
        grindingComputeShader.SetVector("grindDirection", new Vector4(grindDir.x, grindDir.y, grindDir.z, 0));

        // Bounds - always set these
        Bounds bounds = grindable.OriginalWorldBounds;
        grindingComputeShader.SetVector("boundsMin", new Vector4(bounds.min.x, bounds.min.y, bounds.min.z, 0));
        grindingComputeShader.SetVector("boundsMax", new Vector4(bounds.max.x, bounds.max.y, bounds.max.z, 0));
        grindingComputeShader.SetInt("enforceBounds", grindable.EnforceOriginalBounds ? 1 : 0);

        // BVH for mesh-accurate bounds
        bool useBVH = grindable.UseBVHBounds && grindable.EnforceOriginalBounds &&
                      grindable.LinearBVH != null && grindable.LinearBVH.NodeCount > 0;

        grindingComputeShader.SetInt("useBVH", useBVH ? 1 : 0);
        grindingComputeShader.SetInt("bvhNodeCount", useBVH ? grindable.LinearBVH.NodeCount : 0);

        if (useBVH)
        {
            grindable.LinearBVH.CreateBuffers(out bvhNodeBuffer, out bvhTriangleBuffer);
            if (bvhNodeBuffer != null && bvhTriangleBuffer != null)
            {
                grindingComputeShader.SetBuffer(grindKernelHandle, "bvhNodes", bvhNodeBuffer);
                grindingComputeShader.SetBuffer(grindKernelHandle, "bvhTriangles", bvhTriangleBuffer);
            }
            else
            {
                useBVH = false;
                grindingComputeShader.SetInt("useBVH", 0);
                grindingComputeShader.SetInt("bvhNodeCount", 0);
            }
        }

        // Always bind dummy buffers if BVH not used (shader requires all buffers to be bound)
        if (!useBVH)
        {
            bvhNodeBuffer = new ComputeBuffer(1, 32);
            bvhTriangleBuffer = new ComputeBuffer(1, 48);
            grindingComputeShader.SetBuffer(grindKernelHandle, "bvhNodes", bvhNodeBuffer);
            grindingComputeShader.SetBuffer(grindKernelHandle, "bvhTriangles", bvhTriangleBuffer);
        }

        int threadGroups = Mathf.CeilToInt(vertexCount / (float)THREAD_GROUP_SIZE);
        grindingComputeShader.Dispatch(grindKernelHandle, threadGroups, 1, 1);

        Vector4[] results = new Vector4[vertexCount];
        resultBuffer.GetData(results);

        int affectedCount = 0;
        VertexColorUpdater colorUpdater = grindable.colorUpdater;

        for (int i = 0; i < vertexCount; i++)
        {
            if (results[i].w > 0.5f)
            {
                Vector3 worldPos = new Vector3(results[i].x, results[i].y, results[i].z);
                vertices[i] = worldToLocal.MultiplyPoint3x4(worldPos);
                affectedCount++;

                if (colorUpdater != null)
                {
                    colorUpdater.UpdateVertexColor(i, worldPos);
                }
            }
        }

        if (affectedCount > 0)
        {
            grindable.SetVertices(vertices);
            if (colorUpdater != null)
            {
                colorUpdater.ApplyColors();
            }
        }

        return affectedCount;
    }

    private int PerformDrillingGPU(GrindableObject grindable)
    {
        if (!drillComputeInitialized)
        {
            Debug.LogWarning("Drilling compute shader not initialized, falling back to CPU");
            return grindable.ApplyDrilling(drillTool);
        }

        Vector3[] vertices = grindable.GetVertices();
        int vertexCount = vertices.Length;
        if (vertexCount == 0) return 0;

        Vector4[] vertexData = new Vector4[vertexCount];
        Matrix4x4 localToWorld = grindable.transform.localToWorldMatrix;
        Matrix4x4 worldToLocal = grindable.transform.worldToLocalMatrix;

        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 worldPos = localToWorld.MultiplyPoint3x4(vertices[i]);
            vertexData[i] = new Vector4(worldPos.x, worldPos.y, worldPos.z, 0);
        }

        ReleaseBuffers();
        vertexBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 4);
        resultBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 4);
        vertexBuffer.SetData(vertexData);

        drillingComputeShader.SetBuffer(drillKernelHandle, "vertices", vertexBuffer);
        drillingComputeShader.SetBuffer(drillKernelHandle, "results", resultBuffer);
        drillingComputeShader.SetInt("vertexCount", vertexCount);
        drillingComputeShader.SetFloat("drillRadius", drillTool.Radius);
        drillingComputeShader.SetFloat("drillLength", drillTool.EffectiveLength);

        // Pass world-space drill parameters
        Vector3 drillBase = drillTool.DrillBase;
        Vector3 drillDir = drillTool.DrillDirection;
        drillingComputeShader.SetVector("drillBasePos", new Vector4(drillBase.x, drillBase.y, drillBase.z, 0));
        drillingComputeShader.SetVector("drillDirection", new Vector4(drillDir.x, drillDir.y, drillDir.z, 0));

        // Drill mode and speeds
        drillingComputeShader.SetInt("drillMode", (int)drillTool.Mode);
        drillingComputeShader.SetFloat("pushSpeed", drillTool.PushSpeed);
        drillingComputeShader.SetFloat("radialSmoothingSpeed", drillTool.RadialSmoothingSpeed);
        drillingComputeShader.SetFloat("surfaceMargin", drillTool.SurfaceMargin);

        // Bounds
        Bounds bounds = grindable.OriginalWorldBounds;
        drillingComputeShader.SetVector("boundsMin", new Vector4(bounds.min.x, bounds.min.y, bounds.min.z, 0));
        drillingComputeShader.SetVector("boundsMax", new Vector4(bounds.max.x, bounds.max.y, bounds.max.z, 0));
        drillingComputeShader.SetInt("enforceBounds", grindable.EnforceOriginalBounds ? 1 : 0);

        int threadGroups = Mathf.CeilToInt(vertexCount / (float)THREAD_GROUP_SIZE);
        drillingComputeShader.Dispatch(drillKernelHandle, threadGroups, 1, 1);

        Vector4[] results = new Vector4[vertexCount];
        resultBuffer.GetData(results);

        int affectedCount = 0;
        VertexColorUpdater colorUpdater = grindable.colorUpdater;

        for (int i = 0; i < vertexCount; i++)
        {
            if (results[i].w > 0.5f)
            {
                Vector3 worldPos = new Vector3(results[i].x, results[i].y, results[i].z);
                vertices[i] = worldToLocal.MultiplyPoint3x4(worldPos);
                affectedCount++;

                if (colorUpdater != null)
                {
                    colorUpdater.UpdateVertexColor(i, worldPos);
                }
            }
        }

        if (affectedCount > 0)
        {
            grindable.SetVertices(vertices);
            if (colorUpdater != null)
            {
                colorUpdater.ApplyColors();
            }
        }

        return affectedCount;
    }

    private void ReleaseBuffers()
    {
        vertexBuffer?.Release();
        vertexBuffer = null;
        resultBuffer?.Release();
        resultBuffer = null;
        bvhNodeBuffer?.Release();
        bvhNodeBuffer = null;
        bvhTriangleBuffer?.Release();
        bvhTriangleBuffer = null;
    }

    public void ResetAllMeshes()
    {
        foreach (var grindable in grindableObjects)
        {
            grindable?.ResetMesh();
        }
        Debug.Log("All meshes reset");
    }

    public void WeldAllVertices()
    {
        foreach (var grindable in grindableObjects)
        {
            grindable?.WeldVertices();
        }
    }

    public void AddGrindable(GrindableObject grindable)
    {
        if (!grindableObjects.Contains(grindable))
        {
            grindableObjects.Add(grindable);
        }
    }

    public void RemoveGrindable(GrindableObject grindable)
    {
        grindableObjects.Remove(grindable);
    }

    private void OnDestroy()
    {
        ReleaseBuffers();

        if (toggleOperationAction != null)
        {
            toggleOperationAction.performed -= OnToggleOperation;
            toggleOperationAction.Dispose();
        }
        if (resetMeshAction != null)
        {
            resetMeshAction.performed -= OnResetMesh;
            resetMeshAction.Dispose();
        }
        if (weldVerticesAction != null)
        {
            weldVerticesAction.performed -= OnWeldVertices;
            weldVerticesAction.Dispose();
        }
        if (switchModeAction != null)
        {
            switchModeAction.performed -= OnSwitchMode;
            switchModeAction.Dispose();
        }
    }

    private void OnValidate()
    {
        operationInterval = Mathf.Max(0.001f, operationInterval);
        computeShaderThreshold = Mathf.Max(100, computeShaderThreshold);
    }
}