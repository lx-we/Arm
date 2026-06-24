using BigProject.CargoTask;
using BigProject.Communication;
using BigProject.Config;
using BigProject.Devices;
using BigProject.Devices.Arm;
using BigProject.JointMoveRecord;
using BigProject.Logger;
using BigProject.Serials;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BigProject
{
    public class Core
    {
        public string ipBegin = "192.168.1.";
        //设备列表
        public List<BaseDevice> Devices = new List<BaseDevice>();
        //接收的数据
        Byte[] buffer = new Byte[4096];
        //机械臂助手
        public ArmSerial ArmSerial;
        //配置信息
        public ConfigEntity ArmConfig { get; set; }
        //机械臂控制
        public ArmContrl ArmContrl { get; set; }
        //夹爪控制
        public ArmClaw ArmClaw {  get; set; }
        //轴移动记录
        public ObservableCollection<JointRecordModel> JointRecords = new ObservableCollection<JointRecordModel>();
        //货物调度配置
        public CargoTaskConfig CargoTask { get; set; }
        //与 C++ 决策层通信的命名管道服务
        public ArmPipeServer ArmPipe { get; private set; }
        public Core()
        {
            ArmConfig = ConfigResposity.ReadConfigs();
            CargoTask = CargoTaskRes.Read();
            ArmPipe = new ArmPipeServer();
            ArmPipe.Start();
        }


        //初始化机械臂
        public bool InitArm(string comName)
        {
            ArmSerial = new ArmSerial(comName,out bool Resutl);
            if(!Resutl)
            {
                Log.Info($"串口{comName}连接失败");
                return false;
            }
            
            ArmContrl = new ArmContrl(ArmConfig, ArmSerial);
            ArmClaw = new ArmClaw(ArmSerial);
            return true;
        }

        public bool ArmDispose()
        {
            return ArmSerial.SerialDispose();
        }
    }
}
