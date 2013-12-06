using FalconUDP;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace FalconUDPTests
{
    [TestClass()]
    public class ConcurrentGenericObjectPoolTests
    {
        class PunyClass
        {
            int value;
        }

        [TestMethod]
        public void StressTestConcurrentGenericObjectPoolTests()
        {
            var num = 100;
            var returned = 0;
            var pool = new GenericObjectPool<PunyClass>(32);
            BlockingCollection<PunyClass> borrowed = new BlockingCollection<PunyClass>();

            Parallel.For(0, num, i =>
                {
                    var item  = pool.Borrow();
                    borrowed.Add(item);
                });

            while (returned < num)
            {
                PunyClass item;
                borrowed.TryTake(out item, -1);
                returned++;
                pool.Return(item);
            }
        }
    }
}
