// DeformBrush.cs
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.InputSystem;

/// <summary>
/// Interactive deformation brush for push/pull/smooth operations.
/// </summary>
public class DeformBrush : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MeshEditController _editController;
    [SerializeField] private Camera _camera;

    [Header("Brush Settings")]
    [SerializeField] private SDFOperations.DeformationType _brushMode = SDFOperations.DeformationType.Push;
    [SerializeField] private float _brushRadius = 0.1f;
    [SerializeField] private float _brushStrength = 0.01f;
    [SerializeField] private float _brushFalloff = 2f;
    [SerializeField] private LayerMask _raycastLayers = -1;

    [Header("Stroke Settings")]
    [SerializeField] private float _minStrokeDistance = 0.01f;
    [SerializeField] private bool _continuousMode = true;
    [SerializeField] private int _framesBetweenMeshUpdates = 5;

    [Header("Preview")]
    [SerializeField] private bool _showPreview = true;
    [SerializeField] private Color _pushColor = new Color(0f, 0.5f, 1f, 0.5f);
    [SerializeField] private Color _pullColor = new Color(1f, 0.5f, 0f, 0.5f);
    [SerializeField] private Color _smoothColor = new Color(0f, 1f, 0.5f, 0.5f);

    // Input System
    private Vector2 _mousePosition;
    private float _mouseScroll;

    // Preview
    private GameObject _previewObject;
    private MeshRenderer _previewRenderer;

    // Stroke state
    private List<Vector3> _strokePoints = new List<Vector3>();
    private Vector3 _lastStrokePosition;
    private Vector3 _currentNormal;
    private bool _isStroking;
    private int _framesSinceUpdate;
    private bool _isEnabled = true;

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

    public SDFOperations.DeformationType BrushMode
    {
        get => _brushMode;
        set => _brushMode = value;
    }

    public float BrushRadius
    {
        get => _brushRadius;
        set => _brushRadius = Mathf.Max(0.01f, value);
    }

    public float BrushStrength
    {
        get => _brushStrength;
        set => _brushStrength = value;
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

        UpdatePreviewPosition();
        HandleInput();
        UpdatePreviewAppearance();
    }

    private void UpdatePreviewPosition()
    {
        _mousePosition = Mouse.current.position.ReadValue();
        Ray ray = _camera.ScreenPointToRay(_mousePosition);
        
        if (Physics.Raycast(ray, out RaycastHit hit, 100f, _raycastLayers))
        {
            if (_previewObject != null)
            {
                _previewObject.transform.position = hit.point;
                _previewObject.transform.up = hit.normal;
                _previewObject.transform.localScale = Vector3.one * _brushRadius * 2f;
                _previewObject.SetActive(_showPreview);
            }
            
            _currentNormal = hit.normal;
        }
        else
        {
            if (_previewObject != null)
            {
                _previewObject.SetActive(false);
            }
        }
    }

    private void HandleInput()
    {
        // Mode switching with 1, 2, 3 keys
        if (Keyboard.current.digit1Key.wasPressedThisFrame) _brushMode = SDFOperations.DeformationType.Push;
        if (Keyboard.current.digit2Key.wasPressedThisFrame) _brushMode = SDFOperations.DeformationType.Pull;
        if (Keyboard.current.digit3Key.wasPressedThisFrame) _brushMode = SDFOperations.DeformationType.Smooth;

        // Brush size adjustment with mouse scroll
        _mouseScroll = Mouse.current.scroll.y.ReadValue();
        if (Mathf.Abs(_mouseScroll) > 0.01f)
        {
            _brushRadius *= (1f + _mouseScroll * 0.1f);
            _brushRadius = Mathf.Clamp(_brushRadius, 0.01f, 2f);
        }

        // Strength adjustment with shift + scroll
        if (Keyboard.current.leftShiftKey.isPressed && Mathf.Abs(_mouseScroll) > 0.01f)
        {
            _brushStrength *= (1f + _mouseScroll * 0.1f);
            _brushStrength = Mathf.Clamp(_brushStrength, 0.001f, 0.1f);
        }

        // Stroke handling with left mouse button
        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            StartStroke();
        }
        else if (Mouse.current.leftButton.isPressed && _isStroking)
        {
            ContinueStroke();
        }
        else if (Mouse.current.leftButton.wasReleasedThisFrame && _isStroking)
        {
            EndStroke();
        }
    }

    private void StartStroke()
    {
        _mousePosition = Mouse.current.position.ReadValue();
        Ray ray = _camera.ScreenPointToRay(_mousePosition);
        
        if (Physics.Raycast(ray, out RaycastHit hit, 100f, _raycastLayers))
        {
            _isStroking = true;
            _strokePoints.Clear();
            _strokePoints.Add(hit.point);
            _lastStrokePosition = hit.point;
            _currentNormal = hit.normal;
            _framesSinceUpdate = 0;

            // Apply initial point
            ApplyBrushAtPoint(hit.point, hit.normal);
        }
    }

    private void ContinueStroke()
    {
        _mousePosition = Mouse.current.position.ReadValue();
        Ray ray = _camera.ScreenPointToRay(_mousePosition);
        
        if (Physics.Raycast(ray, out RaycastHit hit, 100f, _raycastLayers))
        {
            float distance = Vector3.Distance(hit.point, _lastStrokePosition);
            
            if (distance >= _minStrokeDistance)
            {
                _strokePoints.Add(hit.point);
                _lastStrokePosition = hit.point;
                _currentNormal = hit.normal;

                // Apply brush
                ApplyBrushAtPoint(hit.point, hit.normal);
            }
        }

        // Periodically update mesh
        _framesSinceUpdate++;
        if (_continuousMode && _framesSinceUpdate >= _framesBetweenMeshUpdates)
        {
            _editController.CommitDeformation();
            _framesSinceUpdate = 0;
        }
    }

    private void EndStroke()
    {
        _isStroking = false;
        
        // Final mesh update
        _editController.CommitDeformation();
        
        _strokePoints.Clear();
    }

    private void ApplyBrushAtPoint(Vector3 position, Vector3 normal)
    {
        if (_editController == null || !_editController.IsInitialized)
        {
            return;
        }

        // Invert mode with control key
        var mode = _brushMode;
        if (Keyboard.current.leftCtrlKey.isPressed)
        {
            mode = mode switch
            {
                SDFOperations.DeformationType.Push => SDFOperations.DeformationType.Pull,
                SDFOperations.DeformationType.Pull => SDFOperations.DeformationType.Push,
                _ => mode
            };
        }

        // Direction is surface normal for push/pull
        Vector3 direction = normal;

        _editController.Deform(mode, position, direction, _brushRadius, _brushStrength, _brushFalloff);
    }

    private void UpdatePreviewAppearance()
    {
        if (_previewRenderer == null) return;

        Color color = _brushMode switch
        {
            SDFOperations.DeformationType.Push => _pushColor,
            SDFOperations.DeformationType.Pull => _pullColor,
            SDFOperations.DeformationType.Smooth => _smoothColor,
            _ => _pushColor
        };

        // Invert color when control is held
        if (Keyboard.current.leftCtrlKey.isPressed && _brushMode != SDFOperations.DeformationType.Smooth)
        {
            color = _brushMode == SDFOperations.DeformationType.Push ? _pullColor : _pushColor;
        }

        _previewRenderer.material.color = color;
    }

    private void CreatePreviewObject()
    {
        _previewObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _previewObject.name = "DeformBrushPreview";
        _previewObject.transform.SetParent(transform);
        
        // Remove collider
        DestroyImmediate(_previewObject.GetComponent<Collider>());

        _previewRenderer = _previewObject.GetComponent<MeshRenderer>();
        
        // Create transparent material
        Material mat = new Material(Shader.Find("Standard"));
        mat.SetFloat("_Mode", 3);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;
        mat.color = _pushColor;
        
        _previewRenderer.material = mat;
        _previewRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewRenderer.receiveShadows = false;
    }

    private void OnDestroy()
    {
        if (_previewObject != null)
        {
            DestroyImmediate(_previewObject);
        }
    }
}