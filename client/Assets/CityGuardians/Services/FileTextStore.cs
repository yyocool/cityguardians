using System.IO;
using System.Text;

namespace CityGuardians.Services
{
    /// <summary>폴더 하나에 파일로 저장한다. 쓰는 도중 꺼져도 기존 파일이 깨지지 않게 임시 파일을 거친다.</summary>
    public sealed class FileTextStore : ITextStore
    {
        private readonly string _directory;

        public FileTextStore(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(_directory);
        }

        public string Read(string name)
        {
            string path = Path.Combine(_directory, name);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        public void Write(string name, string text)
        {
            string path = Path.Combine(_directory, name);
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        public void Move(string name, string newName)
        {
            string from = Path.Combine(_directory, name);
            if (!File.Exists(from)) return;
            string to = Path.Combine(_directory, newName);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }
    }
}
