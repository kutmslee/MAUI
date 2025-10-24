using System.Net;
using System.Net.Sockets;
using System.Timers;

namespace MauiApp20251022
{
    public partial class MainPage : ContentPage
    {
        public Socket? socket;
        public EndPoint serverEP;
        private CancellationTokenSource? cts;
        private System.Timers.Timer? timeoutTimer;
        private double remainingSeconds = 15; // PLC timeout
        private readonly double refreshInterval = 1.0; // update every 1 second
        private byte PLCStatus;

        public MainPage()
        {
            InitializeComponent();
            serverEP = new IPEndPoint(IPAddress.Parse("192.168.0.2"), 502);
        }

        private async Task<bool> ConnectWithTimeoutAsync(string ip, int port, int timeoutMs = 2000)
        // 이 함수를 호출하면 비동기 작업인 Task를 반환한다. 이 Task는 bool 타입을 return 할 꺼다.
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);

            try
            {
                var connectTask = socket.ConnectAsync(endpoint);
                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask)
                {
                    // 연결 완료
                    if (socket.Connected)
                        return true;
                }

                // 타임아웃 발생 → 소켓 닫기
                socket.Close();
                socket.Dispose();
                socket = null;
                await SpeakAsync("지금은 피엘씨와 연결할 수 없습니다. 피엘씨와 네트워크 상태를 확인해 주세요.");
                return false;
            }
            catch (Exception)
            {
                socket?.Close();
                socket?.Dispose();
                socket = null;
                return false;
            }
        }
        private async void ConnectButton(object sender, EventArgs e)
        {
            try
            {
                bool connected = await ConnectWithTimeoutAsync("192.168.0.2", 502, 2000); // 2초 제한
                // 비동기 함수 앞에 await를 붙이면, 함수가 완료될 때까지 기다린다.
                if (connected)
                {
                    Label1.Text = "연결 상태 : 접속됨";
                    await SpeakAsync("피엘씨와 십오 초간 연결됩니다.");
                    ReadPLC(sender, e);
                    StartTimeoutCountdown();
                }
            }
            catch (SocketException ex)
            {

                Label1.Text = ex.Message;
                return;
            }
        }

        private void DisconnectButton(object sender, EventArgs e)
        {
            if (cts != null)
            {
                // 이미 읽기 중 → 중지
                StopReading();
            }
            StopTimeoutCountdown();
            Disconnect();
            Label1.Text = "연결 상태 : 접속 끊김";
        }

        private void StartTimeoutCountdown()
        {
            remainingSeconds = 15;
            TimeoutProgress.Progress = 0;
            CountdownLabel.Text = $"PLC 접속 남은 시간 : {remainingSeconds:F0} s";

            timeoutTimer = new System.Timers.Timer(refreshInterval * 1000);
            timeoutTimer.Elapsed += TimeoutTimer_Elapsed;
            timeoutTimer.Start();
        }

        private void StopTimeoutCountdown()
        {
            if (timeoutTimer != null)
            {
                timeoutTimer.Stop();
                timeoutTimer.Dispose();
                timeoutTimer = null;
            }

            TimeoutProgress.Progress = 0;
            CountdownLabel.Text = $"남은시간 : 0 초";
        }

        private void TimeoutTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            remainingSeconds -= refreshInterval;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (remainingSeconds <= 0)
                {
                    TimeoutProgress.Progress = 0;
                    StopTimeoutCountdown();
                    Disconnect();
                }
                else
                {
                    CountdownLabel.Text = $"남은시간 : {remainingSeconds:F0} s";
                    TimeoutProgress.Progress = remainingSeconds / 15.0;
                }
            });
        }

        private async void Disconnect()
        {
            try
            {
                if (socket != null && socket.Connected)
                {
                    StopReading();

                    socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                    await SpeakAsync("피엘씨와의 연결이 종료 되었습니다.");
                }
                else
                {
                    await SpeakAsync("연결이 이미 해제되어 있습니다.");
                }
                Label1.Text = $"연결 상태 : 접속 끊김";
                Label2.Text = $"PLC 상태 : 모름";
            }
            catch (Exception ex)
            {
                Label1.Text = $"Disconnect error: {ex.Message}";
            }
        }

        private void ReadPLC(object sender, EventArgs e)
        {
            if (cts == null) // 읽기 중이 아니면 읽기 시작
            {
                cts = new CancellationTokenSource();
                StartReading();
            }
            else // 이미 읽기 중이면 읽기 중지
            {
                StopReading();
            }
        }

        private void StartReading()
        {
            if (socket == null || !socket.Connected)
                return;

            if (cts == null)
                return;

            var token = cts.Token;  // CancellationTokenSource 에서 토큰을 가져옴
                                    // cts.Token는 취소신호(Cancel)를 기다린다.
                                    //token 변수는 처음에는 false 였다가 cts.Cancel()가 호출되면 true가 된다.

            Task.Run(async () => // 별도의 작업(Task)을 백그라운드 스레드에서 비동기(async)로 실행(Run)
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int id = 0x0000;
                        int length = 0x0006;
                        byte unit = 0x00;
                        byte function = 0x02;
                        int startAddress = 0x0000;
                        int numDataRegisters = 0x0001; //word unit

                        byte[] request;
                        byte[] buffer = new byte[256];
                        int value;

                        request = BuildReadRequest(id, length, unit, function, startAddress, numDataRegisters);
                        socket.Send(request);
                        socket.Receive(buffer);
                        PLCStatus = buffer[9];

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (PLCStatus == 1)
                                Label2.Text = $"PLC 상태 : RUN";
                            else if(PLCStatus == 0)
                                Label2.Text = $"PLC 상태 : STOP";
                        });

                        function = 0x03;
                        request = BuildReadRequest(id, length, unit, function, startAddress, numDataRegisters);
                        if (PLCStatus == 1)
                        {
                            socket.Send(request);
                            socket.Receive(buffer);
                            value = buffer[9] * 256 + buffer[10]; //256은 3번째 바이트라서 16*16곱한 것임.

                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                Label1.Text = $"아날로그 입력값 : {value}";
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            Label1.Text = $"Error: {ex.Message}";
                        });

                        if (ex.Message.Contains("Software caused connection abort"))
                        {
                            Disconnect();
                            break;
                        }
                    }
                    await Task.Delay(100, token); // 0.1초 기다리되, 이 작업(Task)은 token이 취소되면 즉시 종료
                }
            }, token); //token이 취소되면 작업(Task)도 취소됨
        }

        private void StopReading()
        {
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();  // 리소스 정리
                cts = null;
            }
        }

        private async void GreenLampButton(object sender, EventArgs e)
        {
            if (PLCStatus == 0)
            {
                await SpeakAsync("피엘씨가 정지 상태여서 램프를 켤 수 없습니다.");
                return;
            }

            if (socket == null || !socket.Connected)
            {
                Label1.Text = "연결 상태 : 접속 끊김";
                await SpeakAsync("피엘씨와의 연결이 해제되어 있어서 작동할 수 없습니다.");
                return;
            }

            byte[] request;

            int id = 0x0000;
            int length = 0x0006;
            byte unit = 0x00;
            byte function = 0x05;
            int startAddress = 0x0000;
            int value = 0xFF00;
            try
            {
                if (socket.Connected)
                {
                    request = BuildReadRequest(id, length, unit, function, startAddress, value);
                    socket.Send(request);
                    byte[] buf = new byte[12];
                    socket.Receive(buf);
                    button4.BackgroundColor = Colors.Green;
                }
            }
            catch (Exception ex)
            {
                Label1.Text = ex.Message;
                return;
            }

            startAddress = startAddress + 1;
            value = 0x0000;
            try
            {
                if (socket.Connected)
                {
                    request = BuildReadRequest(id, length, unit, function, startAddress, value);
                    socket.Send(request);
                    button5.BackgroundColor = Colors.DarkRed;
                    byte[] buf2 = new byte[12];
                    socket.Receive(buf2);
                }
            }
            catch (Exception ex)
            {
                Label1.Text = "2" + ex.Message;
                return;
            }

            await SpeakAsync("녹색 램프를 켰습니다.");
        }

        private async void RedLampButton(object sender, EventArgs e)
        {
            if (PLCStatus == 0)
            {
                await SpeakAsync("피엘씨가 정지 상태여서 램프를 켤 수 없습니다.");
                return;
            }

            if (socket == null || !socket.Connected)
            {
                Label1.Text = "연결 상태 : 접속 끊김";
                await SpeakAsync("피엘씨와의 연결이 해제되어 있어서 작동할 수 없습니다.");
                return;
            }

            byte[] request;

            int id = 0x0000;
            int length = 0x0006;
            byte unit = 0x00;
            byte function = 0x05;
            int startAddress = 0x0000;
            int value = 0x0000;

            try
            {
                if (socket.Connected)
                {
                    request = BuildReadRequest(id, length, unit, function, startAddress, value);
                    socket.Send(request);
                    button4.BackgroundColor = Colors.DarkGreen;
                    byte[] buf2 = new byte[12];
                    socket.Receive(buf2);
                }
            }
            catch (Exception ex)
            {
                Label1.Text = ex.Message;
                return;
            }

            startAddress = startAddress + 1;
            value = 0xFF00;
            try
            {
                if (socket.Connected)
                {
                    request = BuildReadRequest(id, length, unit, function, startAddress, value);
                    socket.Send(request);
                    button5.BackgroundColor = Colors.Red;
                    byte[] buf2 = new byte[12];
                    socket.Receive(buf2);
                }
            }
            catch (Exception ex)
            {
                Label1.Text = ex.Message;
                return;
            }

            await SpeakAsync("적색 램프를 켰습니다.");
        }

        static byte[] BuildReadRequest(int id, int length, byte unit, byte function, int startAddress, int numDataRegisters)
        {
            byte[] data = new byte[12];

            byte[] _id = BitConverter.GetBytes((short)IPAddress.HostToNetworkOrder((short)id));
            data[0] = _id[0];			    // Slave id high byte
            data[1] = _id[1];				// Slave id low byte
            byte[] _length = BitConverter.GetBytes((short)IPAddress.HostToNetworkOrder((short)length));
            data[4] = _length[0];           // Size of message after this field high byte
            data[5] = _length[1];           // Size of message after this field low byte
            data[6] = unit;					// Slave address
            data[7] = function;				// Function code
            byte[] _adr = BitConverter.GetBytes((short)IPAddress.HostToNetworkOrder((short)startAddress));
            data[8] = _adr[0];				// Start address high byte
            data[9] = _adr[1];				// Start address low byte
            byte[] _numDataRegisters = BitConverter.GetBytes((short)IPAddress.HostToNetworkOrder((short)numDataRegisters));
            data[10] = _numDataRegisters[0]; // Number of data registers high byte
            data[11] = _numDataRegisters[1]; // Number of data registers low byte

            return data;
        }

        static byte[] BuildWriteRequest(int id, int length, byte unit, byte function, int startAddress, int value)
        {
            byte[] data = new byte[12];

            byte[] _id = BitConverter.GetBytes((short)id);
            data[0] = _id[1];			    // Slave id high byte
            data[1] = _id[0];				// Slave id low byte
            byte[] _length = BitConverter.GetBytes((short)IPAddress.HostToNetworkOrder((short)length));
            data[4] = _length[0];           // Size of message after this field high byte
            data[5] = _length[1];           // Size of message after this field low byte
            data[6] = unit;					// Slave address
            data[7] = function;				// Function code
            byte[] _adr = BitConverter.GetBytes((short)IPAddress.HostToNetworkOrder((short)startAddress));
            data[8] = _adr[0];				// Start address
            data[9] = _adr[1];              // Start address
            byte[] _value = BitConverter.GetBytes((short)IPAddress.HostToNetworkOrder((short)value));
            data[10] = _value[0];		    // Value to write
            data[11] = _value[1];           // Value to write

            return data;
        }

        private async Task SpeakAsync(string message)
        {
            try
            {
                await TextToSpeech.SpeakAsync(message);
            }
            catch (Exception ex)
            {
                Label1.Text = $"TTS Error: {ex.Message}";
            }
        }

    }
}
