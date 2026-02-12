#region

using System.Net;
using EvilDICOM.Core.Enums;

#endregion

namespace EvilDICOM.Core.IO.Data
{
    public class DataRestriction
    {
        public static string EnforceLengthRestriction(uint lengthLimit, string data)
        {
            if (data.Length > lengthLimit)
            {
                return data;
            }
            return data;
        }

        public static byte[] EnforceEvenLength(byte[] data, VR vr)
        {
            switch (vr)
            {
                case VR.UniqueIdentifier:
                case VR.OtherByteString:
                case VR.Unknown:
                    return DataPadder.PadNull(data);
                case VR.AgeString:
                case VR.ApplicationEntity:
                case VR.CodeString:
                case VR.Date:
                case VR.DateTime:
                case VR.DecimalString:
                case VR.IntegerString:
                case VR.LongString:
                case VR.LongText:
                case VR.PersonName:
                case VR.ShortString:
                case VR.ShortText:
                case VR.Time:
                case VR.UnlimitedText:
                case VR.UnlimitedCharacter:
                case VR.UniversalResourceId:
                    return DataPadder.PadSpace(data);
                default:
                    return data;
            }
        }

        public static string EnforceUrlEncoding(string originalValue)
        {
            var encoded = WebUtility.UrlEncode(originalValue.TrimEnd(' '));
            return encoded;
        }

        public static bool EnforceRealNonZero(double value, string propertyName)
        {
            if (value == 0 || double.IsNaN(value))
                return false;
            return true;
        }

        public static bool EnforceRealNonZero(int value, string propertyName)
        {
            if (value == 0)
                return false;
            return true;
        }
    }
}