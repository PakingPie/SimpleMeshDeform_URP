using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EvilDICOM.Core;
using EvilDICOM.Core.Element;
using EvilDICOM.Core.Helpers;
using EvilDICOM.Core.Interfaces;
using EvilDICOM.Core.Selection;
using EvilDICOM.Network.Enums;
using static EvilDICOM.Network.Enums.CommandField;

namespace EvilDICOM.Network.DIMSE
{
    public class NActionResponse : AbstractDIMSEResponse
    {
        public NActionResponse(NActionRequest req, Status status)
        {
            this.MessageIDBeingRespondedTo = req.MessageID;
            this.CommandField = (ushort)N_ACTION_RP;
            this.DataSetType = (ushort)CommandDataSetType.EMPTY;
            this.AffectedSOPClassUID = req.RequestedSOPClassUID;
            this.AffectedSOPInstanceUID = req.RequestedSOPInstanceUID;
            this.ActionTypeID = req.ActionTypeID;
            this.Status = (ushort)status;
        }

        public NActionResponse(DICOMObject d)
        {
            CommandField = (ushort)N_ACTION_RP;
            var sel = new DICOMSelector(d);
            GroupLength = sel.CommandGroupLength.Data;
            AffectedSOPClassUID = sel.AffectedSOPClassUID.Data;
            AffectedSOPInstanceUID = sel.AffectedSOPInstanceUID.Data;
            MessageIDBeingRespondedTo = sel.MessageIDBeingRespondedTo.Data;
            DataSetType = sel.CommandDataSetType.Data;
            Status = sel.Status.Data;
            if (sel.ActionTypeID != null)
                ActionTypeID = sel.ActionTypeID.Data;
        }

        protected UnsignedShort _actionTypeId = new UnsignedShort { Tag = TagHelper.ActionTypeID };

        public ushort ActionTypeID
        {
            get { return _actionTypeId.Data; }
            set { _actionTypeId.Data = value; }
        }

        protected UniqueIdentifier _affectedSOPInstanceUID = new UniqueIdentifier { Tag = TagHelper.AffectedSOPInstanceUID };
        public string AffectedSOPInstanceUID
        {
            get { return _affectedSOPInstanceUID.Data; }
            set { _affectedSOPInstanceUID.Data = value; }
        }

        /// <summary>
        ///     The order of elements to send in a IIOD packet
        /// </summary>
        public override List<IDICOMElement> Elements
        {
            get
            {
                return new List<IDICOMElement>
                {
                    _groupLength,
                    _affectedSOPClassUID,
                    _commandField,
                    _messageIdBeingRespondedTo,
                    _dataSetType,
                    _status,
                    _affectedSOPInstanceUID,
                    _actionTypeId
                };
            }
        }
    }
}
