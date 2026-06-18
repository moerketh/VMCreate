using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.MediaHandlers;

namespace VMCreate.Tests.GUI
{
    [TestClass]
    public sealed class DeploymentProgressPresenterTests
    {
        private FakeViewModel _viewModel;
        private SynchronousDispatcher _dispatcher;
        private Mock<ILogger> _logger;
        private DeploymentProgressPresenter _presenter;

        [TestInitialize]
        public void Setup()
        {
            _viewModel = new FakeViewModel();
            _dispatcher = new SynchronousDispatcher();
            _logger = new Mock<ILogger>();
            _presenter = new DeploymentProgressPresenter(
                _viewModel,
                _dispatcher,
                CreateGalleryItem(DiskImageFormat.Iso, false),
                new VmCustomizations(),
                new Dictionary<string, ICustomizationStep>(StringComparer.OrdinalIgnoreCase),
                _logger.Object);
        }

        private static GalleryItem CreateGalleryItem(DiskImageFormat fileType, bool isNativeHyperV)
        {
            string ext = fileType switch
            {
                DiskImageFormat.Iso => ".iso",
                DiskImageFormat.Vmdk => ".vmdk",
                DiskImageFormat.Vhdx => ".vhdx",
                DiskImageFormat.Qcow2 => ".qcow2",
                _ => ".bin"
            };
            return new GalleryItem { DiskUri = $"http://example.com/disk{ext}", IsNativeHyperV = isNativeHyperV };
        }

        [TestMethod]
        public void Present_NullInfo_ReturnsEmptyResult()
        {
            var result = _presenter.Present(null);
            Assert.IsFalse(result.IsError);
            Assert.IsNull(result.ScrollToId);
        }

        [TestMethod]
        public void Present_ErrorMessage_MarksErrorAndFailsActive()
        {
            var info = new CreateVMProgressInfo
            {
                Phase = VmDeploymentPhase.CreateVM,
                ErrorMessage = "disk failure"
            };

            _presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.CreateVM });
            var result = _presenter.Present(info);

            Assert.IsTrue(result.IsError);
            Assert.IsTrue(_viewModel.FailedPhases.ContainsKey(DeployPageViewModel.PhaseCreateVM));
        }

        [TestMethod]
        public void Present_PhaseTransition_ActivatesPhase()
        {
            _presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.Download });

            Assert.AreEqual(DeployPageViewModel.PhaseDownload, _presenter.ActivePhaseId);
            Assert.AreEqual(DeployPageViewModel.PhaseDownload, _viewModel.ActivePhases[0]);
        }

        [TestMethod]
        public void Present_TwoPhases_CompletesFirstThenActivatesSecond()
        {
            _presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.Download });
            _presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.CreateVM });

            Assert.IsTrue(_viewModel.CompletedPhases.Contains(DeployPageViewModel.PhaseDownload));
            Assert.AreEqual(DeployPageViewModel.PhaseCreateVM, _presenter.ActivePhaseId);
        }

        [TestMethod]
        public void Present_DetectedGen2_InsertsCustomizeAndDiskSubSteps()
        {
            var c = new VmCustomizations { ConfigureXrdp = true };
            _presenter = new DeploymentProgressPresenter(
                _viewModel,
                _dispatcher,
                CreateGalleryItem(DiskImageFormat.Vmdk, false),
                c,
                new Dictionary<string, ICustomizationStep>(),
                _logger.Object);

            _presenter.Present(new CreateVMProgressInfo { DetectedGeneration = 2 });

            Assert.IsTrue(_viewModel.CustomizePhaseInserted);
            Assert.AreEqual(2, _viewModel.LastDiskSubStepsGeneration);
            Assert.IsTrue(_viewModel.LastDiskSubStepsNeedsIsoBoot);
        }

        [TestMethod]
        public void Present_DetectedGen1_InsertsMbrPhases()
        {
            _presenter = new DeploymentProgressPresenter(
                _viewModel,
                _dispatcher,
                CreateGalleryItem(DiskImageFormat.Vmdk, false),
                new VmCustomizations(),
                new Dictionary<string, ICustomizationStep>(),
                _logger.Object);

            _presenter.Present(new CreateVMProgressInfo { DetectedGeneration = 1 });

            Assert.IsTrue(_viewModel.MbrPhasesInserted);
            Assert.AreEqual(1, _viewModel.LastDiskSubStepsGeneration);
            Assert.IsTrue(_viewModel.LastDiskSubStepsNeedsIsoBoot);
        }

        [TestMethod]
        public void Present_SubStep_ActivatesSubStepUnderPhase()
        {
            _presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.CreateVM });
            _presenter.Present(new CreateVMProgressInfo { SubStep = VmDeploymentSubStep.ConnectNic });

            Assert.AreEqual(DeployPageViewModel.SubConnectNic, _presenter.ActiveSubStepId);
            Assert.IsTrue(_viewModel.ActivePhases.Contains(DeployPageViewModel.SubConnectNic));
        }

        [TestMethod]
        public void Present_ProgressPercentage_UpdatesPhaseProgress()
        {
            _presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.Download });
            _presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.Download, ProgressPercentage = 42, DownloadSpeed = 1.5 });

            Assert.AreEqual(42, _viewModel.Progress[DeployPageViewModel.PhaseDownload].Percentage);
            StringAssert.Contains(_viewModel.Progress[DeployPageViewModel.PhaseDownload].Text, "MB/s");
        }

        [TestMethod]
        public void Present_VmName_SetsViewModelVmName()
        {
            _presenter.Present(new CreateVMProgressInfo { VmName = "TestVM_20260313" });

            Assert.AreEqual("TestVM_20260313", _viewModel.VmName);
        }

        [TestMethod]
        public void CompleteActive_CompletesActivePhaseAndSubStep()
        {
            _presenter.Present(new CreateVMProgressInfo { Phase = VmDeploymentPhase.CreateVM });
            _presenter.Present(new CreateVMProgressInfo { SubStep = VmDeploymentSubStep.ConnectNic });
            _presenter.CompleteActive();

            Assert.IsTrue(_viewModel.CompletedPhases.Contains(DeployPageViewModel.SubConnectNic));
            Assert.IsTrue(_viewModel.CompletedPhases.Contains(DeployPageViewModel.PhaseCreateVM));
        }

        private sealed class FakeViewModel : IDeploymentProgressViewModel
        {
            public string VmName { get; set; }
            public List<string> ActivePhases { get; } = new();
            public List<string> CompletedPhases { get; } = new();
            public Dictionary<string, string> FailedPhases { get; } = new();
            public Dictionary<string, (int Percentage, string Text)> Progress { get; } = new();

            public bool DownloadCloningIsoPhaseInserted { get; set; }
            public bool PostBootPhaseInserted { get; set; }
            public bool MbrPhasesInserted { get; set; }
            public bool CustomizePhaseInserted { get; set; }
            public bool CleanupIsoBootPhaseInserted { get; set; }
            public int LastDiskSubStepsGeneration { get; set; }
            public bool LastDiskSubStepsNeedsIsoBoot { get; set; }

            public void InsertDownloadCloningIsoPhase() => DownloadCloningIsoPhaseInserted = true;
            public void InsertPostBootPhase() => PostBootPhaseInserted = true;
            public void InsertMbrPhases() => MbrPhasesInserted = true;
            public void InsertDiskSubSteps(int generation, bool needsIsoBoot)
            {
                LastDiskSubStepsGeneration = generation;
                LastDiskSubStepsNeedsIsoBoot = needsIsoBoot;
            }
            public void InsertCustomizePhase() => CustomizePhaseInserted = true;
            public void InsertCleanupIsoBootPhase() => CleanupIsoBootPhaseInserted = true;

            public void ActivatePhase(string id)
            {
                if (!ActivePhases.Contains(id))
                    ActivePhases.Add(id);
            }

            public void CompletePhase(string id)
            {
                if (!CompletedPhases.Contains(id))
                    CompletedPhases.Add(id);
            }

            public void FailPhase(string id, string message) => FailedPhases[id] = message;

            public void UpdatePhaseProgress(string id, int percentage, string progressText) => Progress[id] = (percentage, progressText);
        }

        private sealed class SynchronousDispatcher : IDispatcher
        {
            public void Invoke(Action action) => action();

            public Task InvokeAsync(Action action)
            {
                action();
                return Task.CompletedTask;
            }
        }
    }
}
