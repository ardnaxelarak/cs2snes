using System;
using System.Collections.Generic;
using System.Text;

namespace cs2snes {
    public class SnesException : Exception {
        public SnesException() : base() { }
        public SnesException(string message) : base(message) { }
        public SnesException(string message, Exception innerException) : base(message, innerException) { }
    }
}
