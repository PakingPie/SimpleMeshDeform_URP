// CuttingTool.cs
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Interactive cutting tool for mesh editing.
/// Handles mouse input and preview rendering.
/// </summary>
public class CuttingTool : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MeshEditController _editController;
    [SerializeField] private Camera _camera;

    [Header("Tool Settings")]
    [SerializeField] private SDFOperations.ToolType _toolType = SDFOperations.ToolType.Sphere;
    [SerializeField] private Vector3 _toolScale = Vector3.one * 0.2f;
    [SerializeField] private float _blendRadius = 0.02f;
    [SerializeField] private LayerMask _raycastLayers = -1;

    [Header("Preview")]
    [SerializeField] private Material _previewMaterial;
    [SerializeField] private Color _previewColor = new Color(1f, 0f, 0f, 0.5f);
    [SerializeField] private bool _showPreview = true;

    // Input System
    private Vector2 _mousePosition;

    // Preview object
    private GameObject _previewObject;
    private MeshRenderer _previewRenderer;
    private MeshFilter _previewFilter;

    // State
    private Vector3 _currentPosition;
    private Vector3 _currentRotation;
    private bool _isValid;
    private bool _isEnabled = true;

    public Material PreviewMaterial
    {
        get => _previewMaterial;
        set
        {
            _previewMaterial = value;
            if (_previewRenderer != null)
            {
                _previewRenderer.material = _previewMaterial;
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (_previewObject != null)
            {
                _previewObject.SetActive(value && _showPreview);
            }
        }
    }

    public SDFOperations.ToolType ToolType
    {
        get => _toolType;
        set
        {
            _toolType = value;
            UpdatePreviewMesh();
        }
    }

    public Vector3 ToolScale
    {
        get => _toolScale;
        set => _toolScale = value;
    }

    private void Start()
    {
        if (_camera == null)
        {
            _camera = Camera.main;
        }

        if (_editController == null)
        {
            _editController = GetComponent<MeshEditController>();
        }

        CreatePreviewObject();
    }

    private void Update()
    {
        if (!_isEnabled) return;

        UpdateToolPosition();
        UpdatePreview();
        HandleInput();
    }

    private void UpdateToolPosition()
    {
        _mousePosition = Mouse.current.position.ReadValue();
        Ray ray = _camera.ScreenPointToRay(_mousePosition);
        
        if (Physics.Raycast(ray, out RaycastHit hit, 100f, _raycastLayers))
        {
            _currentPosition = hit.point;
            _isValid = true;

            // Align to surface normal (optional)
            if (Keyboard.current.leftShiftKey.isPressed)
            {
                Vector3 forward = Vector3.Cross(hit.normal, Vector3.up);
                if (forward.sqrMagnitude < 0.001f)
                {
                    forward = Vector3.Cross(hit.normal, Vector3.right);
                }
                _currentRotation = Quaternion.LookRotation(forward, hit.normal).eulerAngles;
            }
        }
        else
        {
            // Place at fixed distance from camera
            _currentPosition = ray.GetPoint(2f);
            _isValid = false;
        }
    }

    private void UpdatePreview()
    {
        if (_previewObject == null) return;

        _previewObject.SetActive(_showPreview && _isEnabled);
        
        if (_showPreview)
        {
            _previewObject.transform.position = _currentPosition;
            _previewObject.transform.eulerAngles = _currentRotation;
            _previewObject.transform.localScale = _toolScale;

            // Change color based on validity
            if (_previewRenderer != null)
            {
                Color color = _isValid ? _previewColor : new Color(0.5f, 0.5f, 0.5f, 0.3f);
                _previewRenderer.material.color = color;
            }
        }
    }

    private void HandleInput()
    {
        if (Mouse.current.leftButton.wasPressedThisFrame && _isValid)
        {
            PerformCut();
        }

        // Scroll to resize
        float scroll = Mouse.current.scroll.y.ReadValue();
        if (Mathf.Abs(scroll) > 0.01f)
        {
            float scaleFactor = 1f + scroll * 0.1f;
            _toolScale *= scaleFactor;
            _toolScale = Vector3.Max(_toolScale, Vector3.one * 0.01f);
        }

        // Number keys for tool type
        if (Keyboard.current.digit1Key.wasPressedThisFrame) ToolType = SDFOperations.ToolType.Sphere;
        if (Keyboard.current.digit2Key.wasPressedThisFrame) ToolType = SDFOperations.ToolType.Box;
        if (Keyboard.current.digit3Key.wasPressedThisFrame) ToolType = SDFOperations.ToolType.Cylinder;
    }

    private void PerformCut()
    {
        if (_editController == null || !_editController.IsInitialized)
        {
            Debug.LogWarning("CuttingTool: Edit controller not ready.");
            return;
        }

        _editController.Cut(_toolType, _currentPosition, _currentRotation, _toolScale, _blendRadius);
    }

    private void CreatePreviewObject()
    {
        _previewObject = new GameObject("CuttingToolPreview");
        _previewObject.transform.SetParent(transform);

        _previewFilter = _previewObject.AddComponent<MeshFilter>();
        _previewRenderer = _previewObject.AddComponent<MeshRenderer>();

        // Create preview material if not assigned
        if (_previewMaterial == null)
        {
            _previewMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            _previewMaterial.SetFloat("_Mode", 3); // Transparent
            _previewMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _previewMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _previewMaterial.SetInt("_ZWrite", 0);
            _previewMaterial.DisableKeyword("_ALPHATEST_ON");
            _previewMaterial.EnableKeyword("_ALPHABLEND_ON");
            _previewMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            _previewMaterial.renderQueue = 3000;
        }

        _previewRenderer.material = _previewMaterial;
        _previewRenderer.material.color = _previewColor;
        _previewRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewRenderer.receiveShadows = false;

        UpdatePreviewMesh();
    }

    private void UpdatePreviewMesh()
    {
        if (_previewFilter == null) return;

        Mesh mesh = _toolType switch
        {
            SDFOperations.ToolType.Sphere => CreateSphereMesh(),
            SDFOperations.ToolType.Box => CreateBoxMesh(),
            SDFOperations.ToolType.Cylinder => CreateCylinderMesh(),
            _ => CreateSphereMesh()
        };

        _previewFilter.sharedMesh = mesh;
    }

    private Mesh CreateSphereMesh()
    {
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Mesh mesh = Instantiate(temp.GetComponent<MeshFilter>().sharedMesh);
        DestroyImmediate(temp);
        return mesh;
    }

    private Mesh CreateBoxMesh()
    {
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh mesh = Instantiate(temp.GetComponent<MeshFilter>().sharedMesh);
        DestroyImmediate(temp);
        return mesh;
    }

    private Mesh CreateCylinderMesh()
    {
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Mesh mesh = Instantiate(temp.GetComponent<MeshFilter>().sharedMesh);
        DestroyImmediate(temp);
        return mesh;
    }

    private void OnDestroy()
    {
        if (_previewObject != null)
        {
            DestroyImmediate(_previewObject);
        }
    }
}