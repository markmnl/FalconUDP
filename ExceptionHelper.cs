using System;

namespace FalconUDP
{
    internal static class ExceptionHelper
    {
        internal static string GetFullDetails(this Exception ex)
        {
            string msg = "";
            do
            {
                msg += String.Format("{0}{1}{0}{2}", Environment.NewLine, ex.Message, ex.StackTrace);
                ex = ex.InnerException;
            } while (ex != null);

            return msg;
        }
    }
}
