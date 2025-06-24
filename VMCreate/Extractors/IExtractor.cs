using System;
using System.Threading;
using VMCreate;

public interface IExtractor
{
    void Extract(string zipFilePath, string extractPath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo);
}