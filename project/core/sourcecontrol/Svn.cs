using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Exortech.NetReflector;
using ThoughtWorks.CruiseControl.Core.Config;
using ThoughtWorks.CruiseControl.Core.Util;

namespace ThoughtWorks.CruiseControl.Core.Sourcecontrol
{
	[ReflectorType("svn")]
	public class Svn : ProcessSourceControl
	{
		public const string DefaultExecutable = "svn.exe";
		public static readonly string UtcXmlDateFormat = "yyyy-MM-ddTHH:mm:ssZ";

		public Svn(ProcessExecutor executor, IHistoryParser parser, IFileSystem fileSystem) : base(parser, executor)
		{
			this.fileSystem = fileSystem;
		}

		public Svn() : this(new ProcessExecutor(), new SvnHistoryParser(), new SystemIoFileSystem())
		{
		}

		[ReflectorProperty("webUrlBuilder", InstanceTypeKey="type", Required = false)]
		public IModificationUrlBuilder UrlBuilder;

		[ReflectorProperty("executable", Required = false)]
		public string Executable = DefaultExecutable;

		[ReflectorProperty("trunkUrl", Required = false)]
		public string TrunkUrl;

		[ReflectorProperty("workingDirectory", Required = false)]
		public string WorkingDirectory;

		[ReflectorProperty("tagOnSuccess", Required = false)]
		public bool TagOnSuccess = false;

		[ReflectorProperty("tagBaseUrl", Required = false)]
		public string TagBaseUrl;

		[ReflectorProperty("username", Required = false)]
		public string Username;

		[ReflectorProperty("password", Required = false)]
		public string Password;

		[ReflectorProperty("autoGetSource", Required = false)]
		public bool AutoGetSource = true;

		[ReflectorProperty("checkExternals", Required = false)]
		public bool CheckExternals = false;

		private readonly IFileSystem fileSystem;

		/// <summary>
		/// Modifications discovered by this instance of the source control interface.
		/// This is needed for the Multi Source Control block. (See CCNET-639/CCNET-1307)
		/// </summary>
		internal Modification[] mods = new Modification[0];

		public string FormatCommandDate(DateTime date)
		{
			return date.ToUniversalTime().ToString(UtcXmlDateFormat, CultureInfo.InvariantCulture);
		}

		public override Modification[] GetModifications(IIntegrationResult from, IIntegrationResult to)
		{
			List<Modification> modifications = new List<Modification>();
			List<string> directories = new List<string>();
			directories.Add(TrunkUrl);

			if (CheckExternals)
			{
				List<string> externals = ParseExternalsDirectories(Execute(PropGetProcessInfo(to)));
				foreach (string external in externals)
				{
					if (!directories.Contains(external)) directories.Add(external);
				}
			}
			
			foreach (string directory in directories)
			{
				Log.Debug(directory);
				ProcessResult result = Execute(NewHistoryProcessInfo(from, to, directory));
				mods = ParseModifications(result, from.StartTime, to.StartTime);
				if (mods != null)
				{
					modifications.AddRange(mods);
				}
			}

			mods = modifications.ToArray();

			if (UrlBuilder != null)
			{
				UrlBuilder.SetupModification(mods);
			}
			FillIssueUrl(mods);

			return mods;
		}

		public override void LabelSourceControl(IIntegrationResult result)
		{
			if (TagOnSuccess && result.Succeeded)
			{
				Execute(NewLabelProcessInfo(result));
			}
		}

		public override void GetSource(IIntegrationResult result)
		{
            result.BuildProgressInformation.SignalStartRunTask("Getting source from SVN");

			if (! AutoGetSource) return;

			if (DoesSvnDirectoryExist(result))
			{
				UpdateSource(result);
			}
			else
			{
				CheckoutSource(result);
			}
		}

		private void CheckoutSource(IIntegrationResult result)
		{
			if (StringUtil.IsBlank(TrunkUrl))
				throw new ConfigurationException(
					"<trunkurl> configuration element must be specified in order to automatically checkout source from SVN.");
			Execute(NewCheckoutProcessInfo(result));
		}

		private ProcessInfo NewCheckoutProcessInfo(IIntegrationResult result)
		{
			ProcessArgumentBuilder buffer = new ProcessArgumentBuilder();
			buffer.AddArgument("checkout");
			buffer.AddArgument(TrunkUrl);
			buffer.AddArgument(result.BaseFromWorkingDirectory(WorkingDirectory));
			AppendCommonSwitches(buffer);
			return NewProcessInfo(buffer.ToString(), result);
		}

		private ProcessInfo PropGetProcessInfo(IIntegrationResult result)
		{
			ProcessArgumentBuilder buffer = new ProcessArgumentBuilder();
			buffer.AddArgument("propget");
			buffer.AddArgument("-R");
			AppendCommonSwitches(buffer);
			buffer.AddArgument("svn:externals");
			buffer.AddArgument(TrunkUrl);
			return NewProcessInfo(buffer.ToString(), result);
		}

		private void UpdateSource(IIntegrationResult result)
		{
			Execute(NewGetSourceProcessInfo(result));
		}

		private bool DoesSvnDirectoryExist(IIntegrationResult result)
		{
			string svnDirectory = Path.Combine(result.BaseFromWorkingDirectory(WorkingDirectory), ".svn");
			string underscoreSvnDirectory = Path.Combine(result.BaseFromWorkingDirectory(WorkingDirectory), "_svn");
			return fileSystem.DirectoryExists(svnDirectory) || fileSystem.DirectoryExists(underscoreSvnDirectory);
		}

		private ProcessInfo NewGetSourceProcessInfo(IIntegrationResult result)
		{
			ProcessArgumentBuilder buffer = new ProcessArgumentBuilder();
			buffer.AddArgument("update");
            AppendRevision(buffer, Modification.GetLastChangeNumber(mods));
			AppendCommonSwitches(buffer);
			return NewProcessInfo(buffer.ToString(), result);
		}

//		TAG_COMMAND_FORMAT = "copy --message "CCNET build label" "trunkUrl" "tagBaseUrl/label"
		private ProcessInfo NewLabelProcessInfo(IIntegrationResult result)
		{
			ProcessArgumentBuilder buffer = new ProcessArgumentBuilder();
			buffer.AddArgument("copy");
			buffer.AppendArgument(TagMessage(result.Label));
			buffer.AddArgument(TagSource(result));
			buffer.AddArgument(TagDestination(result.Label));
            AppendRevision(buffer, Modification.GetLastChangeNumber(mods));
			AppendCommonSwitches(buffer);
			return NewProcessInfo(buffer.ToString(), result);
		}

//		HISTORY_COMMAND_FORMAT = "log TrunkUrl --revision \"{{{StartDate}}}:{{{EndDate}}}\" --verbose --xml --non-interactive";
		private ProcessInfo NewHistoryProcessInfo(IIntegrationResult from, IIntegrationResult to, string url)
		{
			ProcessArgumentBuilder buffer = new ProcessArgumentBuilder();
			buffer.AddArgument("log");
			buffer.AddArgument(url);
			buffer.AppendArgument(
				string.Format("-r \"{{{0}}}:{{{1}}}\"", FormatCommandDate(from.StartTime), FormatCommandDate(to.StartTime)));
			buffer.AppendArgument("--verbose --xml");
			AppendCommonSwitches(buffer);
			return NewProcessInfo(buffer.ToString(), to);
		}

		private static string TagMessage(string label)
		{
			return string.Format("-m \"CCNET build {0}\"", label);
		}

		private string TagSource(IIntegrationResult result)
		{
            if (Modification.GetLastChangeNumber(mods) == 0)
			{
				return result.BaseFromWorkingDirectory(WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar);
			}
			return TrunkUrl;
		}

		private string TagDestination(string label)
		{
			return string.Format("{0}/{1}", TagBaseUrl, label);
		}

		private void AppendCommonSwitches(ProcessArgumentBuilder buffer)
		{
			buffer.AddArgument("--username", Username);
			buffer.AddArgument("--password", Password);
			buffer.AddArgument("--non-interactive");
			buffer.AddArgument("--no-auth-cache");
		}

		private static void AppendRevision(ProcessArgumentBuilder buffer, int revision)
		{
			buffer.AppendIf(revision > 0, "--revision {0}", revision.ToString());
		}

		private static List<string> ParseExternalsDirectories(ProcessResult result)
		{
			List<string> externalDirectories = new List<string>();

			using (StringReader reader = new StringReader(result.StandardOutput))
			{
				string externalsDefinition;

				while ((externalsDefinition = reader.ReadLine()) != null)
				{
					string[] tokens = externalsDefinition.Split(new string[] { " - " }, StringSplitOptions.None);
					if (tokens.Length > 1)
					{
						externalsDefinition = tokens[1];
					}
					tokens = externalsDefinition.Split(' ');

					foreach (string url in tokens)
					{
						if (IsSvnUrl(url) && !url.Contains("-r") && !externalDirectories.Contains(url))
						{
							externalDirectories.Add(url);
						}
					}
				}
			}

			return externalDirectories;
		} 

		private static bool IsSvnUrl(string url)
		{
			return (url.StartsWith("http") || url.StartsWith("svn") || url.StartsWith("file"));
		}

		private ProcessInfo NewProcessInfo(string args, IIntegrationResult result)
		{
			string workingDirectory = result.BaseFromWorkingDirectory(WorkingDirectory);
			if (!Directory.Exists(workingDirectory)) Directory.CreateDirectory(workingDirectory);

			ProcessInfo processInfo = new ProcessInfo(Executable, args, workingDirectory);
			processInfo.StreamEncoding = Encoding.UTF8;
			return processInfo;
		}
	}
}
