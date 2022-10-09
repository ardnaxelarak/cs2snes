using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace cs2snes {
    public class Snes {
        private const int ROM_START = 0x000000;
        private const int WRAM_START = 0xF50000;
        private const int WRAM_SIZE = 0x020000;
        private const int SRAM_START = 0xE00000;

        private readonly Encoding ENCODING = Encoding.UTF8;
        private readonly SemaphoreSlim opLock = new(1, 1);
        private readonly byte[] PutBytePrefix = new byte[] { 0x00, 0xE2, 0x20, 0x48, 0xEB, 0x48 };
        private readonly byte[] PutByteSuffix = new byte[] { 0xA9, 0x00, 0x8F, 0x00, 0x2C, 0x00, 0x68, 0xEB, 0x68, 0x28, 0x6C, 0xEA, 0xFF, 0x08 };


        private ClientWebSocket socket = new();
        private bool socketErrored = false;
        private readonly ArraySegment<byte> byteBuffer = new(new byte[0x1000]);
        private bool isSd2Snes = false;
        private string device;
        private TimeSpan timeout = TimeSpan.FromSeconds(1);

        public void SetTimeout(TimeSpan timeout) {
            this.timeout = timeout;
        }

        public WebSocketState State {
            get {
                if (socketErrored) {
                    return WebSocketState.Aborted;
                }
                return socket.State;
            }
        }

        public async Task Connect(string address = "ws://localhost:8080") {
            if (State == WebSocketState.Open) {
                return;
            }

            try {
                socket = new();
                socketErrored = false;
                CancellationTokenSource source = new(timeout);
                await socket.ConnectAsync(new Uri(address), source.Token);
            } catch (WebSocketException ex) {
                socketErrored = true;
                throw new SnesException("Unable to connect.", ex);
            } catch (TaskCanceledException ex) {
                throw new SnesException("Unable to connect.", ex);
            }
        }

        public async Task<List<string>> ListDevices() {
            await opLock.WaitAsync();
            try {
                CheckOpen();

                Request request = new() {
                    Opcode = "DeviceList",
                    Space = "SNES",
                };

                await Send(request);

                Response response = await Receive();

                return response.Results;
            } finally {
                opLock.Release();
            }
        }

        public async Task Attach(string device) {
            CheckOpen();

            Request request = new() {
                Opcode = "Attach",
                Space = "SNES",
                Operands = { device },
            };

            await Send(request);

            if (device.ToLower().Contains("sd2snes") || device.ToLower().Contains("fxpakpro") || (device.Length == 4 && device.StartsWith("COM"))) {
                isSd2Snes = true;
            } else {
                isSd2Snes = false;
            }

            this.device = device;
        }

        public async Task<DeviceInfo> Info() {
            await opLock.WaitAsync();
            try {
                CheckOpen();

                Request request = new() {
                    Opcode = "Info",
                    Space = "SNES",
                    Operands = { this.device },
                };

                await Send(request);

                return DeviceInfo.FromResponse(await Receive());
            } finally {
                opLock.Release();
            }
        }

        public async Task Name(string name) {
            CheckOpen();

            Request request = new() {
                Opcode = "Name",
                Space = "SNES",
                Operands = { name },
            };

            await Send(request);
        }

        public async Task Boot(string rom) {
            CheckOpen();

            Request request = new() {
                Opcode = "Boot",
                Space = "SNES",
                Operands = { rom },
            };

            await Send(request);
        }

        public async Task Menu() {
            CheckOpen();

            Request request = new() {
                Opcode = "Menu",
                Space = "SNES",
            };

            await Send(request);
        }

        public async Task Reset() {
            CheckOpen();

            Request request = new() {
                Opcode = "Reset",
                Space = "SNES",
            };

            await Send(request);
        }

        public async Task<List<byte>> ReadMemory(int address, int size) {
            await opLock.WaitAsync();
            try {
                CheckOpen();

                Request request = new() {
                    Opcode = "GetAddress",
                    Space = "SNES",
                    Operands = { address.ToString("X"), size.ToString("X") },
                };

                await Send(request);

                var response = await ReceiveBytes();

                if (response.Count != size) {
                    throw new SnesException($"Error reading 0x{address:X6}: expected 0x{size:X} bytes, got 0x{response.Count:X}.");
                }

                return response;
            } finally {
                opLock.Release();
            }
        }

        public async Task WriteMemory(List<DeviceWrite> writes) {
            await opLock.WaitAsync();
            try {
                CheckOpen();

                Request request = new() {
                    Opcode = "PutAddress",
                };

                if (isSd2Snes) {
                    var cmd = new List<byte>(PutBytePrefix);

                    foreach (var write in writes) {
                        if (write.Address < WRAM_START || write.Address + write.Bytes.Count > WRAM_START + WRAM_SIZE) {
                            throw new SnesException($"Write at 0x{write.Address:X6} for 0x{write.Bytes.Count:X} bytes out of range.");
                        }
                        int deviceAddress = write.Address - WRAM_START + 0x7E0000;
                        for (var offset = 0; offset < write.Bytes.Count; offset++) {
                            cmd.Add(0xA9); // LDA
                            cmd.Add(write.Bytes[offset]);
                            cmd.Add(0x8F); // STA.l
                            cmd.Add((byte) (0xFF & ((deviceAddress + offset) >> 0)));
                            cmd.Add((byte) (0xFF & ((deviceAddress + offset) >> 8)));
                            cmd.Add((byte) (0xFF & ((deviceAddress + offset) >> 16)));
                        }
                    }

                    cmd.AddRange(PutByteSuffix);

                    request.Space = "CMD";
                    request.Operands = new List<string> { "2C00", (cmd.Count - 1).ToString("X"), "2C00", "1" };

                    await Send(request);
                    await SendBytes(cmd);
                } else {
                    request.Space = "SNES";
                    foreach (var write in writes) {
                        request.Operands = new() { write.Address.ToString("X6"), write.Bytes.Count.ToString("X") };
                        await Send(request);
                        await SendBytes(write.Bytes);
                    }
                }
            } finally {
                opLock.Release();
            }
        }

        public async Task Close() {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Gwaa", CancellationToken.None);
        }

        private void CheckOpen() {
            if (State != WebSocketState.Open) {
                throw new SnesException("Websocket is not open.");
            }
        }

        private async Task Send(object data) {
            try {
                CancellationTokenSource source = new(timeout);
                await socket.SendAsync(
                    new(ENCODING.GetBytes(JsonConvert.SerializeObject(data))),
                    WebSocketMessageType.Text,
                    true,
                    source.Token);
            } catch (WebSocketException ex) {
                socketErrored = true;
                throw new SnesException("Sending data failed.", ex);
            } catch (ObjectDisposedException ex) {
                throw new SnesException("Sending data failed.", ex);
            }
        }

        private async Task SendBytes(List<byte> data) {
            try {
                Console.WriteLine(string.Join(" ", data.Select(b => b.ToString("X2"))));
                CancellationTokenSource source = new(timeout);
                await socket.SendAsync(
                    data.ToArray(),
                    WebSocketMessageType.Binary,
                    true,
                    source.Token);
            } catch (WebSocketException ex) {
                socketErrored = true;
                throw new SnesException("Sending data failed.", ex);
            } catch (ObjectDisposedException ex) {
                throw new SnesException("Sending data failed.", ex);
            }
        }

        private async Task<Response> Receive() {
            try {
                CancellationTokenSource source = new(timeout);
                WebSocketReceiveResult result = await socket.ReceiveAsync(byteBuffer, source.Token);
                string response = ENCODING.GetString(byteBuffer.Array, 0, result.Count);
                return JsonConvert.DeserializeObject<Response>(response);
            } catch (WebSocketException ex) {
                socketErrored = true;
                throw new SnesException("Receiving data failed.", ex);
            } catch (ObjectDisposedException ex) {
                throw new SnesException("Receiving data failed.", ex);
            }
        }

        private async Task<List<byte>> ReceiveBytes() {
            try {
                CancellationTokenSource source = new(timeout);
                WebSocketReceiveResult result = await socket.ReceiveAsync(byteBuffer, source.Token);
                List<byte> response = byteBuffer.Slice(0, result.Count).ToList();
                return response;
            } catch (WebSocketException ex) {
                socketErrored = true;
                throw new SnesException("Receiving data failed.", ex);
            } catch (ObjectDisposedException ex) {
                throw new SnesException("Receiving data failed.", ex);
            }
        }
    }
}
