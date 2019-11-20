using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeltaWebMapCanvas.Entities
{
    public class IncomingMessage
    {
        public IncomingMessageOpcode opcode;
        public JObject payload;
    }

    public enum IncomingMessageOpcode
    {
        SwitchCanvas = 0,
        UnsubscribeCanvas = 1,
        ClearCanvas = 2,
        Ping = 3
    }
}
