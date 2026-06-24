using BigProject.CargoTask;
using BigProject.Devices.Arm;
using BigProject.Logger;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BigProject.Communication
{
    /// <summary>
    /// 与 C++ 决策层 arm_controller 对接的命名管道服务（C++ 为 Client，本程序为 Server）。
    /// 协议：LOAD/UNLOAD 命令 → LOAD_DONE/UNLOAD_DONE/ERROR 响应，空格分隔，换行结尾。
    /// </summary>
    public class ArmPipeServer
    {
        public const string DefaultPipeName = "agv_arm_pipe";

        public event Action<bool> ClientConnectedChanged;
        public event Action<string, string, int, int, int> WorkStarted;
        public event Action<string, bool, string> WorkFinished;

        public bool ClientConnected { get; private set; }
        public bool IsBusy => _busy;
        public string LastCommand { get; private set; } = "-";
        public string LastResult { get; private set; } = "-";

        private readonly string _pipeName;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private volatile bool _busy;

        public ArmPipeServer(string pipeName = DefaultPipeName)
        {
            _pipeName = pipeName;
        }

        public void Start()
        {
            if (_listenTask != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            Log.Info($"机械臂管道服务已启动: \\\\.\\pipe\\{_pipeName}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _listenTask?.Wait(2000);
            }
            catch (AggregateException)
            {
            }
            _listenTask = null;
        }

        private void ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous))
                    {
                        pipe.WaitForConnection();
                        SetClientConnected(true);
                        Log.Info("决策层已连接命名管道");
                        HandleClient(pipe, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    Thread.Sleep(500);
                }
            }

            SetClientConnected(false);
        }

        private void SetClientConnected(bool connected)
        {
            if (ClientConnected == connected)
            {
                return;
            }

            ClientConnected = connected;
            ClientConnectedChanged?.Invoke(connected);
        }

        /// <summary>
        /// 界面手动触发装/卸货，走与管道相同的执行逻辑。
        /// </summary>
        public bool RunManualWork(string cmd, int agvId, int taskId, int cargoNum, out string message)
        {
            message = string.Empty;
            if (_busy)
            {
                message = "机械臂任务执行中，请稍后再试。";
                return false;
            }

            if (cmd != "LOAD" && cmd != "UNLOAD")
            {
                message = "未知命令。";
                return false;
            }

            _busy = true;

            try
            {
                bool ok = RunWorkBody(cmd, agvId, taskId, cargoNum, out string responseLine);
                message = ok ? "执行完成。" : "执行失败，请检查坐标与机械臂状态。";
                LastResult = responseLine;
                WorkFinished?.Invoke(responseLine, ok, message);
                return ok;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                message = ex.Message;
                LastResult = $"ERROR {agvId} {taskId} {cargoNum}";
                WorkFinished?.Invoke(LastResult, false, message);
                return false;
            }
            finally
            {
                _busy = false;
            }
        }

        private void HandleClient(NamedPipeServerStream pipe, CancellationToken token)
        {
            var rxBuffer = new StringBuilder();

            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                if (!pipe.CanRead)
                {
                    Thread.Sleep(20);
                    continue;
                }

                var buffer = new byte[256];
                int bytesRead;
                try
                {
                    bytesRead = pipe.Read(buffer, 0, buffer.Length);
                }
                catch (IOException)
                {
                    break;
                }

                if (bytesRead <= 0)
                {
                    Thread.Sleep(20);
                    continue;
                }

                rxBuffer.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));

                int newlinePos;
                while ((newlinePos = rxBuffer.ToString().IndexOf('\n')) >= 0)
                {
                    var line = rxBuffer.ToString(0, newlinePos).Trim('\r');
                    rxBuffer.Remove(0, newlinePos + 1);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    Log.Info($"[ARM RX] {line}");
                    HandleCommandLine(pipe, line);
                }
            }

            Log.Info("决策层管道连接已断开");
            SetClientConnected(false);
        }

        private void HandleCommandLine(NamedPipeServerStream pipe, string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                SendLine(pipe, "ERROR 0 0 0");
                return;
            }

            var cmd = parts[0];
            if (!int.TryParse(parts[1], out int agvId) ||
                !int.TryParse(parts[2], out int taskId) ||
                !int.TryParse(parts[3], out int cargoNum))
            {
                SendLine(pipe, "ERROR 0 0 0");
                return;
            }

            if (_busy)
            {
                SendLine(pipe, $"ERROR {agvId} {taskId} {cargoNum}");
                return;
            }

            if (cmd != "LOAD" && cmd != "UNLOAD")
            {
                SendLine(pipe, $"ERROR {agvId} {taskId} {cargoNum}");
                return;
            }

            _busy = true;
            Task.Run(() => ExecuteWork(pipe, cmd, agvId, taskId, cargoNum));
        }

        private void ExecuteWork(
            NamedPipeServerStream pipe,
            string cmd,
            int agvId,
            int taskId,
            int cargoNum)
        {
            try
            {
                bool ok = RunWorkBody(cmd, agvId, taskId, cargoNum, out string responseLine);
                SendLine(pipe, responseLine);
                WorkFinished?.Invoke(responseLine, ok, ok ? "执行完成。" : "执行失败。");
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                var errorLine = $"ERROR {agvId} {taskId} {cargoNum}";
                SendLine(pipe, errorLine);
                WorkFinished?.Invoke(errorLine, false, ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private bool RunWorkBody(
            string cmd,
            int agvId,
            int taskId,
            int cargoNum,
            out string responseLine)
        {
            var doneCmd = cmd == "LOAD" ? "LOAD_DONE" : "UNLOAD_DONE";
            LastCommand = $"{cmd} {agvId} {taskId} {cargoNum}";
            WorkStarted?.Invoke(cmd, doneCmd, agvId, taskId, cargoNum);

            if (!IsArmReady())
            {
                Log.Info("[ARM] 机械臂未连接，返回 ERROR");
                responseLine = $"ERROR {agvId} {taskId} {cargoNum}";
                LastResult = responseLine;
                return false;
            }

            var config = App.Core.CargoTask ?? new CargoTaskConfig();
            bool ok = cmd == "LOAD"
                ? ExecuteLoad(agvId, config)
                : ExecuteUnload(agvId, config);

            responseLine = ok
                ? $"{doneCmd} {agvId} {taskId} {cargoNum}"
                : $"ERROR {agvId} {taskId} {cargoNum}";
            LastResult = responseLine;
            return ok;
        }

        private static bool IsArmReady()
        {
            return App.Core?.ArmContrl != null && App.Core?.ArmSerial != null;
        }

        private static bool ExecuteLoad(int agvId, CargoTaskConfig config)
        {
            if (!TryGetAgvPosition(agvId, config, out double carX, out double carY, out double carZ))
            {
                Log.Info($"[ARM] 未知 AGV id: {agvId}");
                return false;
            }

            var arm = App.Core.ArmContrl;
            // 装货：堆货点取货 → 放到 AGV
            if (!MoveToPose(arm, config.StackX, config.StackY, config.StackZ))
            {
                return false;
            }
            CloseClaw();
            if (!MoveToPose(arm, carX, carY, carZ))
            {
                return false;
            }
            OpenClaw();
            return true;
        }

        private static bool ExecuteUnload(int agvId, CargoTaskConfig config)
        {
            if (!TryGetAgvPosition(agvId, config, out double carX, out double carY, out double carZ))
            {
                Log.Info($"[ARM] 未知 AGV id: {agvId}");
                return false;
            }

            var arm = App.Core.ArmContrl;
            // 卸货：从 AGV 取货 → 放到堆货点
            if (!MoveToPose(arm, carX, carY, carZ))
            {
                return false;
            }
            CloseClaw();
            if (!MoveToPose(arm, config.StackX, config.StackY, config.StackZ))
            {
                return false;
            }
            OpenClaw();
            return true;
        }

        private static bool TryGetAgvPosition(
            int agvId,
            CargoTaskConfig config,
            out double x,
            out double y,
            out double z)
        {
            switch (agvId)
            {
                case 1:
                    x = config.Car1X; y = config.Car1Y; z = config.Car1Z;
                    return true;
                case 2:
                    x = config.Car2X; y = config.Car2Y; z = config.Car2Z;
                    return true;
                case 3:
                    x = config.Car3X; y = config.Car3Y; z = config.Car3Z;
                    return true;
                default:
                    x = y = z = 0;
                    return false;
            }
        }

        private static bool MoveToPose(ArmContrl arm, double x, double y, double z)
        {
            if (!arm.MoveL(x, y, z, 0, 0, 0))
            {
                Log.Info($"[ARM] 逆解失败: ({x},{y},{z})");
                return false;
            }

            arm.MoveJoints();
            return WaitMoveComplete(arm);
        }

        private static bool WaitMoveComplete(ArmContrl arm, int timeoutMs = 120000)
        {
            return arm.WaitForMotionComplete();
        }

        private static void CloseClaw()
        {
            App.Core.ArmClaw?.SetAngle(70);
            Thread.Sleep(1500);
        }

        private static void OpenClaw()
        {
            App.Core.ArmClaw?.SetAngle(0);
            Thread.Sleep(1000);
        }

        private static void SendLine(NamedPipeServerStream pipe, string line)
        {
            try
            {
                if (pipe == null || !pipe.IsConnected)
                {
                    return;
                }

                var payload = Encoding.ASCII.GetBytes(line + "\n");
                pipe.Write(payload, 0, payload.Length);
                pipe.Flush();
                Log.Info($"[ARM TX] {line}");
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }
    }
}
