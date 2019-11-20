using System;
using System.Collections.Generic;
using System.Text;

namespace DeltaWebMapCanvas.Entities
{
    public class CanvasClearMessage : BaseMessage
    {
        public string cleared_by_name;
        public string cleared_by_icon;
    }
}
