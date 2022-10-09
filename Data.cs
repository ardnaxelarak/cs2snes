using Newtonsoft.Json;
using System.Collections.Generic;

namespace cs2snes {
    internal class Request {
        public string Opcode { get; set; }
        public string Space { get; set; }
        public List<string> Operands { get; set; } = new();
    }

    internal class Response {
        public List<string> Results { get; set; } = new();
    }

    public class DeviceInfo {
        public string FirmwareVersion { get; set; }
        public string VersionString { get; set; }
        public string Rom { get; set; }
        public string Flags { get; set; }

        static internal DeviceInfo FromResponse(Response response) {
            return new DeviceInfo {
                FirmwareVersion = response.Results[0],
                VersionString = response.Results[1],
                Rom = response.Results[2],
                Flags = response.Results.Count > 4 ? response.Results[3] : "",
            };
        }
    }

    public class DeviceWrite {
        public int Address { get; set; }
        public List<byte> Bytes { get; set; } = new();
    }
}
