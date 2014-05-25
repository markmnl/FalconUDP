using System;

namespace FalconUDP
{
    /// <summary>
    /// Delegate used as callback once a Falcon operation completes.
    /// </summary>
    /// <param name="result"><see cref="FalconOperationResult{T}"/> with the result of the operation.</param>
    /// <typeparam name="TReturnValue">The type <see cref="FalconOperationResult{T}.ReturnValue"/> will be.</typeparam>
    public delegate void FalconOperationCallback<TReturnValue>(FalconOperationResult<TReturnValue> result);

    /// <summary>
    /// Result of a Falcon operation, successful or not.
    /// </summary>
    public class FalconOperationResult<TReturnValue>
    {
        /// <summary>
        /// True if the operation was successul, otherwise false.
        /// </summary>
        public bool Success { get; private set; }

        /// <summary>
        /// Failure reason. Always set when <see cref="Success"/> is false.
        /// </summary>
        public string NonSuccessMessage { get; private set; }

        /// <summary>
        /// Set if an Exception was the cause of opertaion to fail. Only set when <see cref="Success"/> is false.
        /// </summary>
        public Exception Exception { get; private set; }

        /// <summary>
        /// Return value from the operation.
        /// </summary>
        public TReturnValue ReturnValue { get; private set; }

        internal FalconOperationResult(bool success, string nonSuccessMessage, Exception ex, TReturnValue returnValue)
        {
            this.Success = success;
            this.NonSuccessMessage = nonSuccessMessage;
            this.Exception = ex;
            this.ReturnValue = returnValue;
        }

        internal FalconOperationResult(bool success, string nonSuccessMessage, TReturnValue returnValue)
            : this(success, nonSuccessMessage, null, returnValue)
        {
        }

        internal FalconOperationResult(bool success, TReturnValue returnValue)
            : this(success, null, null, returnValue)
        {
        }

        internal FalconOperationResult(Exception ex, TReturnValue returnValue)
            : this(false, null, ex, returnValue)
        {
            string msg = "";
            do
            {
                msg += String.Format("{0}{1}{0}{2}", Environment.NewLine, ex.Message, ex.StackTrace);
                ex = ex.InnerException;
            } while(ex != null);

            this.NonSuccessMessage = msg;
        }
    }
}
