namespace VMCreate.CLI
{
    public static class ExitCodes
    {
        public const int Success = 0;
        public const int GeneralError = 1;
        public const int InvalidArguments = 2;
        public const int ImageNotFound = 3;
        public const int VmCreationFailed = 4;
        public const int HyperVError = 5;
        public const int Cancelled = 6;
    }
}
