using System;
using System.Collections.Generic;
using System.Text;

namespace DeltaWebMapCanvas.Entities
{
    public enum WebsocketMessageOpcode
    {
        DefineUserID = 0,
        DefineUserColor = 1,
        SetStateFlag = 2,
        ClearCanvas = 3
    }
}
