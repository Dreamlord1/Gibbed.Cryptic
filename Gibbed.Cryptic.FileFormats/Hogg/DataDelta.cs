﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Gibbed.Cryptic.FileFormats.Hogg
{
    internal class DataDelta
    {
        public byte Action;
        public int Id;
        public byte[] Data;
    }
}
