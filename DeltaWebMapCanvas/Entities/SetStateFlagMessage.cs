using System;
using System.Collections.Generic;
using System.Text;

namespace DeltaWebMapCanvas.Entities
{
    public class SetStateFlagMessage : BaseMessage
    {
        public int state; //0: not ready, 1: ready
        public string resume_token;
    }
}
