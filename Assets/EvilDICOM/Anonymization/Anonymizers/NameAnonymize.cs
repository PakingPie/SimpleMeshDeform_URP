#region

using EvilDICOM.Core;
using EvilDICOM.Core.Enums;

#endregion

namespace EvilDICOM.Anonymization.Anonymizers
{
    /// <summary>
    /// Removes all names from the DICOM File. If using PatientIdAnonymizer, call this first so new id is not removed
    /// </summary>
    public class NameAnonymizer : IAnonymizer
    {
        public void Anonymize(DICOMObject d)
        {
            foreach (var name in d.FindAll(VR.PersonName))
                name.DData = "Anonymized";
        }
    }
}