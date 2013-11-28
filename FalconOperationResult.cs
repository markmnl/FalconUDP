using System;

namespace FalconUDP
{
    /// <summary>
    /// Delegate used as callback once a Falcon operation completes.
    /// </summary>
    /// <param name="result"><see cref="FalconOperationResult"/> with the result of the operation.</param>
    public delegate void FalconOperationCallback(FalconOperationResult result);

    /// <summary>
    /// Result of a Falcon operation, successful or not.
    /// </summary>
    public class FalconOperationResult
    {
        /// <summary>
        /// Successul operation.
        /// </summary>
        public static readonly FalconOperationResult SuccessResult = new FalconOperationResult(true, null);
        
        /// <summary>
        /// True if the operation was successul, otherwise false.
        /// </summary>
        public bool Success { get; private set; }

        /// <summary>
        /// Failure reason. Always set when <see cref="Success"/> if false.
        /// </summary>
        public string NonSuccessMessage { get; private set; }

        /// <summary>
        /// Set if an Exception was the cause of opertaion to fail. Only set when <see cref="Success"/> is false.
        /// </summary>
        public Exception Exception { get; private set; }

        /// <summary>
        /// A user token set depending on the operation.
        /// </summary>
        public object Tag { get; private set; }

        internal FalconOperationResult(bool success, string nonSuccessMessage, Exception ex, object tag)
        {
            this.Success = success;
            this.NonSuccessMessage = nonSuccessMessage;
            this.Exception = ex;
            this.Tag = tag;
        }

        internal FalconOperationResult(bool success, string nonSuccessMessage)
            : this(success, nonSuccessMessage, null, null)
        {
        }

        internal FalconOperationResult(bool success, object tag)
            : this(success, null, null, tag)
        {
        }

        internal FalconOperationResult(Exception ex)
            : this(false, ex.Message, ex, null)
        {
        }
    }
}
