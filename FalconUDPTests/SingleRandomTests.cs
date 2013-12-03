using FalconUDP;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FalconUDPTests
{
    [TestClass]
    public class SingleRandomTests
    {
        [TestMethod]
        public void SingleRandomStressTest()
        {
            const int ITERATIONS = 1000000;
            ConcurrentBag<int> ints = new ConcurrentBag<int>();
            ConcurrentBag<double> doubles = new ConcurrentBag<double>();
            ManualResetEvent waitHandel = new ManualResetEvent(false);

            Parallel.For(1, ITERATIONS, i =>
                {
                    switch (i % 3)
                    {
                        case 0: 
                            var a = SingleRandom.Next();
                            ints.Add(a);
                            break;
                        case 1: 
                            var b = SingleRandom.NextDouble();
                            doubles.Add(b);
                            break;
                        case 2: 
                            var c = SingleRandom.Next(1, 100);
                            ints.Add(c);
                            break;
                    }

                    if (i == ITERATIONS)
                        waitHandel.Set();
                    
                });

            // do something with collections to ensure they are not completely optimised away
            Assert.IsTrue(ints.Count > 0);
            Assert.IsTrue(doubles.Count > 0);
        }
    }
}
