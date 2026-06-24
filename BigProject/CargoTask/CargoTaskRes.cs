using Newtonsoft.Json;
using System.IO;
using System.Text;

namespace BigProject.CargoTask
{
    public class CargoTaskRes
    {
        private static string DataDic = "./DATA";
        private static string ReadConfigFile = $"{DataDic}/CargoTask.data";

        public static CargoTaskConfig Read()
        {
            lock (DataDic)
            {
                if (!Directory.Exists(DataDic))
                {
                    Directory.CreateDirectory(DataDic);
                }
                if (!File.Exists(ReadConfigFile))
                {
                    using (File.Create(ReadConfigFile)) { }
                }
                var json = File.ReadAllText(ReadConfigFile, Encoding.UTF8);
                var conf = JsonConvert.DeserializeObject<CargoTaskConfig>(json);
                if (conf == null)
                {
                    return new CargoTaskConfig();
                }
                return conf;
            }
        }

        public static void Write(CargoTaskConfig config)
        {
            lock (DataDic)
            {
                if (!Directory.Exists(DataDic))
                {
                    Directory.CreateDirectory(DataDic);
                }
                using (FileStream fileStream = File.Create(ReadConfigFile))
                {
                    var str = JsonConvert.SerializeObject(config, Formatting.Indented);
                    byte[] by = Encoding.UTF8.GetBytes(str);
                    fileStream.Write(by, 0, by.Length);
                }
            }
        }
    }
}
