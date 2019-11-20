using System;
using System.Collections.Generic;
using System.Text;

namespace DeltaWebMapCanvas.Entities
{
    public class DefineUserIDMessage : BaseMessage
    {
        public int index;
        public string name;
        public string icon;
        public string id;
        public string color;
    }
}
