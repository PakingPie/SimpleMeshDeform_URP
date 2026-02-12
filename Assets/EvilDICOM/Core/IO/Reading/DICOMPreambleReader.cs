#region

using System.Linq;
using EvilDICOM.Core.Enums;

#endregion

namespace EvilDICOM.Core.IO.Reading
{
    /// <summary>
    ///     This class can read the DICOM preamble consisting of 128 null bits followed by the ASCII characters DICM.
    /// </summary>
    public static class DICOMPreambleReader
    {
        /// <summary>
        ///     Reads the first 132 bits of a file to check if it contains the DICOM preamble.
        /// </summary>
        /// <param name="dr">a stream containing the bits of the file</param>
        /// <returns>a boolean indicating whether or not the DICOM preamble was in the file</returns>
        public static bool Read(DICOMBinaryReader dr)
        {
            if (dr.StreamLength > 132)
            {
                var nullPreamble = dr.Take(128);
                //READ D I C M
                var dcm = dr.Take(4);
                if (dcm[0] != 'D' || dcm[1] != 'I' || dcm[2] != 'C' || dcm[3] != 'M')
                {
                    dr.StreamPosition -= 132; //Rewind
                    return false;
                }
            }
            return true;
        }
    }
}