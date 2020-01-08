using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WebCamCapPic.Utils
{
    class ComReleaser : IDisposable
    {
        private List<object> items = new List<object>();

        public void Add(object obj)
        {
            items.Add(obj);
        }

        public void Dispose()
        {
            foreach (var one in items)
            {
                Marshal.ReleaseComObject(one);
            }

            items.Clear();
        }
    }
}
