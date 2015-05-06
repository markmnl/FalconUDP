using System.Threading.Tasks;

namespace FalconUDPTests
{
    static class TaskHelper
    {
        public static void Sleep(int milliseconds)
        {
            var task = Task.Delay(milliseconds);
            task.Wait();
        }
    }
}
