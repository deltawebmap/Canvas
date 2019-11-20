using System;
using System.Collections.Generic;
using System.Text;

namespace DeltaWebMapCanvas.Entities
{
    public class WebsocketMessageContainer
    {
        public WebsocketMessageOpcode opcode;
        public BaseMessage payload;
    }
}
