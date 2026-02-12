using System;
using System.Threading.Tasks;
using UnityEngine;
using EvilDICOM.Core;
using EvilDICOM.Core.Helpers;
using Vector3 = UnityEngine.Vector3;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class DICOMProcessor : MonoBehaviour
{
    public string folderPath;
    private string[] _filePaths;
    public int DICOMSliceIndex = 0;
    public GameObject VolumeObject;
    public int ImageWidth;
    public int ImageHeight;
    public int ImageDepth;
    [Range(1, 10)]
    public int ImageGap = 1;

    public bool EnableGaussianFilter = false;
    [Range(1, 3)]
    public int GaussianKernelSize = 1;
    public bool GenerateGradientTexture = false;

    private Texture3D _volumeDataTexture;
    private Texture3D _gradientTexture;

    // 用于存储后台计算结果的中间数据
    private float[] _volumeData;
    private float[] _gradientData;
    private int _texWidth;
    private int _texHeight;
    private int _texDepth;
    private float _minVal;
    private float _maxVal;

    public GameObject LoadingPanel;
    private DICOMObject _firstDICOM;
    // Add these two fields to the class
    private float _maxGradientMagnitude;
    private readonly object _gradientMaxLock = new object();
    public void InitializeFilePaths()
    {
        _filePaths = System.IO.Directory.GetFiles(folderPath, "*.dcm");
    }
#if UNITY_EDITOR
    private void OnValidate()
    {
        InitializeFilePaths();
        DICOMSliceIndex = Mathf.Clamp(DICOMSliceIndex, 0, _filePaths.Length - 1);
    }
#endif
    public async void LoadDICOMMultiSlices()
    {
        var progress = new Progress<float>(p =>
        {
            if (LoadingPanel != null)
            {
                LoadingPanel.GetComponent<Image>().material.SetFloat("_Value", p);
            }
        });
        await LoadDICOMImageMultiSlicesAsync(progress);

        if (_firstDICOM != null)
        {
            var sel = _firstDICOM.GetSelector();
        }
    }

    public async Task LoadDICOMImageMultiSlicesAsync(IProgress<float> progress = null)
    {
        int sliceCount = _filePaths.Length;
        if (sliceCount == 0)
        {
            Debug.LogError("No DICOM files provided.");
            return;
        }

        // 在主线程读取第一个文件获取元数据
        DICOMObject firstDICOM = DICOMObject.Read(_filePaths[0]);
        var sel = firstDICOM.GetSelector();
        var rows = (UInt16)firstDICOM.FindFirst(TagHelper.Rows).DData;
        var cols = (UInt16)firstDICOM.FindFirst(TagHelper.Columns).DData;
        var windowCenter = firstDICOM.FindFirst(TagHelper.WindowCenter)?.DData;
        var windowWidth = firstDICOM.FindFirst(TagHelper.WindowWidth)?.DData;
        var imageOrientationPatient = sel.ImageOrientationPatient.Data_;
        var spacing = (float)(Double)firstDICOM.FindFirst(TagHelper.SliceThickness).DData;

        float rescaleIntercept = (float)(Double)firstDICOM.FindFirst(TagHelper.RescaleIntercept).DData;
        float rescaleSlope = (float)(Double)firstDICOM.FindFirst(TagHelper.RescaleSlope).DData;

        ImageWidth = rows;
        ImageHeight = cols;
        ImageDepth = sliceCount;

        _texWidth = ImageWidth / ImageGap;
        _texHeight = ImageHeight / ImageGap;
        _texDepth = ImageDepth / ImageGap;

        // 复制需要在后台线程使用的数据
        double[] orientationCopy = imageOrientationPatient.ToArray();

        // 后台线程处理所有耗时计算
        await Task.Run(() =>
        {
            ProcessVolumeData(sliceCount, rows, cols, rescaleIntercept, rescaleSlope, orientationCopy, progress);
        });

        // 回到主线程创建纹理并上传数据
        await CreateAndUploadTexturesAsync();
    }

    private void ProcessVolumeData(int sliceCount, int rows, int cols,
        float rescaleIntercept, float rescaleSlope, double[] imageOrientationPatient,
        IProgress<float> progress)
    {
        _volumeData = new float[_texWidth * _texHeight * _texDepth];
        _minVal = float.MaxValue;
        _maxVal = float.MinValue;

        // 第一遍：读取所有数据并计算 min/max
        float[][] allHuData = new float[sliceCount][];

        int processedSlices = 0;
        int totalSlices = (sliceCount + ImageGap - 1) / ImageGap;

        for (int i = 0; i < sliceCount; i += ImageGap)
        {
            string path = _filePaths[i];
            DICOMObject dcm = DICOMObject.Read(path);
            var pixelData = dcm.FindFirst(TagHelper.PixelData).DData_;

            float[] huList = new float[rows * cols];
            for (int y = 0; y < cols; y++)
            {
                for (int x = 0; x < rows; x++)
                {
                    int pixelIndex = y * rows + x;
                    float raw = (Byte)pixelData[pixelIndex * 2]
          + 256 * (Byte)pixelData[pixelIndex * 2 + 1];

                    // Handle signed pixel representation (PixelRepresentation = 1)
                    // Uncomment if your DICOM uses signed 16-bit:
                    // if (raw > 32767) raw -= 65536;

                    float hu = rescaleIntercept + rescaleSlope * raw;
                    hu = Mathf.Clamp(hu, -1024f, 3071f);  // clamp AFTER conversion to HU

                    _minVal = Mathf.Min(_minVal, hu);
                    _maxVal = Mathf.Max(_maxVal, hu);
                    huList[pixelIndex] = hu;
                }
            }
            allHuData[i] = huList;

            processedSlices++;
            progress?.Report((float)processedSlices / totalSlices * 0.5f); // 前50%进度
        }

        // 第二遍：归一化并填充体积数据
        float range = _maxVal - _minVal;
        processedSlices = 0;

        for (int i = 0; i < sliceCount; i += ImageGap)
        {
            float[] huList = allHuData[i];

            for (int y = 0, y_cols = 0; y < cols; y += ImageGap, y_cols++)
            {
                for (int x = 0, x_rows = 0; x < rows; x += ImageGap, x_rows++)
                {
                    int _z = sliceCount / ImageGap - i / ImageGap - 1;
                    int _x = x_rows;
                    int _y = y_cols;

                    if (imageOrientationPatient[0] == -1) _x = rows / ImageGap - x_rows - 1;
                    if (imageOrientationPatient[1] == 1) _x = y_cols;
                    if (imageOrientationPatient[1] == -1) _x = cols / ImageGap - y_cols - 1;
                    if (imageOrientationPatient[2] == 1) _x = i / ImageGap;
                    if (imageOrientationPatient[2] == -1) _x = sliceCount / ImageGap - i / ImageGap - 1;

                    if (imageOrientationPatient[3] == 1) _y = x_rows;
                    if (imageOrientationPatient[3] == -1) _y = rows / ImageGap - x_rows - 1;
                    if (imageOrientationPatient[4] == -1) _y = cols / ImageGap - y_cols - 1;
                    if (imageOrientationPatient[5] == 1) _y = i / ImageGap;
                    if (imageOrientationPatient[5] == -1) _y = sliceCount / ImageGap - i / ImageGap - 1;

                    // 边界检查
                    _x = Math.Clamp(_x, 0, _texWidth - 1);
                    _y = Math.Clamp(_y, 0, _texHeight - 1);
                    _z = Math.Clamp(_z, 0, _texDepth - 1);

                    int pixelIndex = x + y * rows;
                    float hu = (huList[pixelIndex] - _minVal) / range;

                    int volumeIndex = _x + _y * _texWidth + _z * _texWidth * _texHeight;
                    _volumeData[volumeIndex] = hu;
                }
            }

            processedSlices++;
            progress?.Report(0.5f + (float)processedSlices / totalSlices * 0.2f); // 50%-70%进度
        }

        // 高斯滤波
        if (EnableGaussianFilter)
        {
            ApplyGaussianFilter(progress);
        }

        // 生成梯度纹理
        if (GenerateGradientTexture)
        {
            GenerateGradientData(progress);
        }

        progress?.Report(1.0f);
    }

    private void ApplyGaussianFilter(IProgress<float> progress)
    {
        float[] filteredData = new float[_volumeData.Length];
        int totalVoxels = _texWidth * _texHeight * _texDepth;
        int processedVoxels = 0;

        // 使用并行处理加速
        Parallel.For(0, _texDepth, z =>
        {
            for (int y = 0; y < _texHeight; y++)
            {
                for (int x = 0; x < _texWidth; x++)
                {
                    float sum = 0.0f;
                    float weightSum = 0.0f;

                    for (int kz = -GaussianKernelSize; kz <= GaussianKernelSize; kz++)
                    {
                        for (int ky = -GaussianKernelSize; ky <= GaussianKernelSize; ky++)
                        {
                            for (int kx = -GaussianKernelSize; kx <= GaussianKernelSize; kx++)
                            {
                                int sampleX = Math.Clamp(x + kx, 0, _texWidth - 1);
                                int sampleY = Math.Clamp(y + ky, 0, _texHeight - 1);
                                int sampleZ = Math.Clamp(z + kz, 0, _texDepth - 1);

                                float weight = Mathf.Exp(-(kx * kx + ky * ky + kz * kz) / (2.0f * GaussianKernelSize * GaussianKernelSize));
                                int sampleIndex = sampleX + sampleY * _texWidth + sampleZ * _texWidth * _texHeight;
                                sum += _volumeData[sampleIndex] * weight;
                                weightSum += weight;
                            }
                        }
                    }

                    int index = x + y * _texWidth + z * _texWidth * _texHeight;
                    filteredData[index] = sum / weightSum;
                }
            }

            // 线程安全的进度更新
            System.Threading.Interlocked.Add(ref processedVoxels, _texWidth * _texHeight);
        });

        _volumeData = filteredData;
    }

    private void GenerateGradientData(IProgress<float> progress)
    {
        _gradientData = new float[_texWidth * _texHeight * _texDepth * 4];
        _maxGradientMagnitude = 0f;

        Parallel.For(0, _texDepth, z =>
        {
            float localMax = 0f;

            for (int y = 0; y < _texHeight; y++)
            {
                for (int x = 0; x < _texWidth; x++)
                {
                    int idx = x + y * _texWidth + z * _texWidth * _texHeight;

                    // Sample neighbors from the NORMALIZED [0,1] volume data
                    float xp = _volumeData[Math.Min(x + 1, _texWidth - 1) + y * _texWidth + z * _texWidth * _texHeight];
                    float xn = _volumeData[Math.Max(x - 1, 0) + y * _texWidth + z * _texWidth * _texHeight];
                    float yp = _volumeData[x + Math.Min(y + 1, _texHeight - 1) * _texWidth + z * _texWidth * _texHeight];
                    float yn = _volumeData[x + Math.Max(y - 1, 0) * _texWidth + z * _texWidth * _texHeight];
                    float zp = _volumeData[x + y * _texWidth + Math.Min(z + 1, _texDepth - 1) * _texWidth * _texHeight];
                    float zn = _volumeData[x + y * _texWidth + Math.Max(z - 1, 0) * _texWidth * _texHeight];

                    // Simple central differences: (forward - backward) / 2
                    // Same sign convention as the shader's CentralDiffGradient
                    // Values range [-0.5, 0.5] per component
                    float gx = (xp - xn) * 0.5f;
                    float gy = (yp - yn) * 0.5f;
                    float gz = (zp - zn) * 0.5f;

                    float mag = Mathf.Sqrt(gx * gx + gy * gy + gz * gz);
                    localMax = Mathf.Max(localMax, mag);

                    int gi = idx * 4;
                    _gradientData[gi] = gx;   // raw — RGBAHalf handles [-0.5, 0.5]
                    _gradientData[gi + 1] = gy;
                    _gradientData[gi + 2] = gz;
                    _gradientData[gi + 3] = _volumeData[idx]; // density for 2D TF
                }
            }

            // Thread-safe max update
            lock (_gradientMaxLock)
            {
                if (localMax > _maxGradientMagnitude)
                    _maxGradientMagnitude = localMax;
            }
        });

        Debug.Log($"Gradient texture: max magnitude = {_maxGradientMagnitude:F4}");
    }

    private async Task CreateAndUploadTexturesAsync()
    {
        // 创建体积纹理
        _volumeDataTexture = new Texture3D(_texWidth, _texHeight, _texDepth, TextureFormat.RHalf, false);
        _volumeDataTexture.filterMode = FilterMode.Bilinear;
        _volumeDataTexture.wrapMode = TextureWrapMode.Clamp;

        // 分批上传数据以避免卡顿
        int batchSize = _texWidth * _texHeight * 4; // 每次处理几个切片
        Color[] colors = new Color[_volumeData.Length];

        for (int i = 0; i < _volumeData.Length; i++)
        {
            colors[i] = new Color(_volumeData[i], _volumeData[i], _volumeData[i]);
        }

        // 分批设置像素
        int totalPixels = colors.Length;
        for (int offset = 0; offset < totalPixels; offset += batchSize)
        {
            int count = Math.Min(batchSize, totalPixels - offset);
            // 这里直接设置，因为 SetPixels 比逐个 SetPixel 快很多
            if (offset == 0)
            {
                _volumeDataTexture.SetPixels(colors);
                break; // SetPixels 会设置所有像素
            }
            await Task.Yield(); // 让出主线程一帧
        }

        _volumeDataTexture.Apply();
        var mat = VolumeObject.GetComponent<MeshRenderer>().sharedMaterial;
        mat.SetTexture("_VolumeDataTexture", _volumeDataTexture);

        // ---- SET WINDOWING PARAMETERS ----
        float originWidth = _maxVal - _minVal;
        float originCenter = (_maxVal + _minVal) * 0.5f;
        mat.SetFloat("_OriginWindowWidth", originWidth);
        mat.SetFloat("_OriginWindowCenter", originCenter);

        // Sensible default viewing window (soft-tissue)
        mat.SetFloat("_WindowCenter", 40f);
        mat.SetFloat("_WindowWidth", 400f);

        Debug.Log($"Volume HU range: [{_minVal:F0}, {_maxVal:F0}]  " + $"→ OriginCenter={originCenter:F0}, OriginWidth={originWidth:F0}");

        if (GenerateGradientTexture && _gradientData != null)
        {
            _gradientTexture = new Texture3D(_texWidth, _texHeight, _texDepth, TextureFormat.RGBAHalf, false);
            _gradientTexture.filterMode = FilterMode.Bilinear;
            _gradientTexture.wrapMode = TextureWrapMode.Clamp;

            Color[] gradColors = new Color[_texWidth * _texHeight * _texDepth];
            for (int i = 0; i < gradColors.Length; i++)
            {
                int gi = i * 4;
                gradColors[i] = new Color(_gradientData[gi], _gradientData[gi + 1],
                                          _gradientData[gi + 2], _gradientData[gi + 3]);
            }

            _gradientTexture.SetPixels(gradColors);

            await Task.Yield();

            _gradientTexture.Apply();
            mat.SetTexture("_GradientTexture", _gradientTexture);
            mat.EnableKeyword("GRADIENT_TEXTURE");

            // ---- AUTO-SET gradient magnitude so normGrad spans [0, 1] properly ----
            float safeMax = Mathf.Max(_maxGradientMagnitude, 0.01f);
            mat.SetFloat("_MaxGradientMagnitude", safeMax);
            Debug.Log($"Set _MaxGradientMagnitude = {safeMax:F4}");
        }
    }

    public void SetTexture3DToDesignatedObject(GameObject go, int rows, int columns, int sliceCount, float spacing, float spaceBetweenSlice, float[] imagePositionPatient)
    {
        go.transform.localScale = new Vector3(rows / 1000.0f, columns / 1000.0f, sliceCount * spacing * Mathf.Abs(spaceBetweenSlice) / 1000.0f);
        Vector3 newPos = new Vector3((float)imagePositionPatient[0], (float)imagePositionPatient[1], (float)imagePositionPatient[2]) / 1000.0f;
        go.transform.localPosition = newPos;
    }

    public Vector3 GetGrad(float[] data, int x, int y, int z,
                        int dimX, int dimY, int dimZ,
                        float minVal, float maxRange)
    {
        float x1 = data[Math.Min(x + 1, dimX - 1) + y * dimX + z * dimX * dimY] - minVal;
        float x2 = data[Math.Max(x - 1, 0) + y * dimX + z * dimX * dimY] - minVal;
        float y1 = data[x + Math.Min(y + 1, dimY - 1) * dimX + z * dimX * dimY] - minVal;
        float y2 = data[x + Math.Max(y - 1, 0) * dimX + z * dimX * dimY] - minVal;
        float z1 = data[x + y * dimX + Math.Min(z + 1, dimZ - 1) * dimX * dimY] - minVal;
        float z2 = data[x + y * dimX + Math.Max(z - 1, 0) * dimX * dimY] - minVal;

        // FIX: divide by 2 for proper central differences
        float invScale = 1.0f / (2.0f * maxRange);
        return new Vector3((x2 - x1) * invScale,
                           (y2 - y1) * invScale,
                           (z2 - z1) * invScale);
    }

    public void CleanUpDICOMData()
    {
        if (_volumeDataTexture != null)
        {
            DestroyImmediate(_volumeDataTexture);
            _volumeDataTexture = null;
        }
        if (_gradientTexture != null)
        {
            DestroyImmediate(_gradientTexture);
            _gradientTexture = null;
        }
        _volumeData = null;
        _gradientData = null;
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(DICOMProcessor))]
public class DICOMProcessorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        DICOMProcessor processor = (DICOMProcessor)target;
        if (GUILayout.Button("Load DICOM Images"))
        {
            processor.LoadDICOMMultiSlices();
        }

        if (GUILayout.Button("Clean Up DICOM Data"))
        {
            processor.CleanUpDICOMData();
        }
    }
}
#endif