using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SIGVerse.Common;
using System.Collections;
using SIGVerse.RosBridge;
using SIGVerse.FCSC.Common;
using System.Drawing;
using System.Drawing.Imaging;

namespace SIGVerse.FCSC.InteractiveCustomerService
{
	[Serializable]
	public class RelocatableObjectInfo
	{
		public string name;
		public Vector3 position;
		public Vector3 eulerAngles;
	}

	[Serializable]
	public class TaskInfo
	{
		public string message;
		public bool   hasImage;
		public string targetName;
	}

	public class SpeechInfo
	{
		public string message;
		public string gender;
		public bool   canCancel;

		public SpeechInfo(string message, string gender, bool canCancel)
		{
			this.message   = message;
			this.gender    = gender;
			this.canCancel = canCancel;
		}
	}

	public class IcsModeratorTool
	{
		private const string TaskInfoFileNameFormat  = "/../SIGVerseConfig/InteractiveCustomerService/TaskInfo{0:D2}.json";
		private const string TaskImageFileNameFormat = "/../SIGVerseConfig/InteractiveCustomerService/TaskImage{0:D2}.jpg";

//		private const string TagModerator = "Moderator";
		private const string TagGraspable = "Graspable";

		//private const string JudgeTriggerNameIn   = "JudgeTriggerIn";
		//private const string DeliveryPositionName = "DeliveryPosition";

		//private const float  DeliveryThreshold = 0.3f;

		public const string SpeechExePath  = "../TTS/ConsoleSimpleTTS.exe";
		public const string SpeechLanguage = "409";
		public const string SpeechGenderModerator = "Male";
		public const string SpeechGenderHsr       = "Female";
		public const int    MaxMessageLength = 1000;

		private IRosConnection[] rosConnections;

		private TaskInfo  taskInfo;
		private Texture2D flippedTaskImage = null;

		private GameObject targetItem;
		private List<GameObject> graspables;

		//private bool? isPlacementSucceeded;

		private IcsPlaybackRecorder playbackRecorder;

		private System.Diagnostics.Process speechProcess;

		private Queue<SpeechInfo> speechInfoQue;
		private SpeechInfo latestSpeechInfo;

		private bool isSpeechUsed;


		public IcsModeratorTool(IcsModerator moderator)
		{
			IcsConfig.Instance.InclementNumberOfTrials();

			GetGameObjects(moderator);

			Initialize();
		}


		private void GetGameObjects(IcsModerator moderator)
		{
			this.graspables = GameObject.FindGameObjectsWithTag(TagGraspable).ToList<GameObject>();

			// Check the name conflict of graspables.
			if(this.graspables.Count != (from graspable in this.graspables select graspable.name).Distinct().Count())
			{
				throw new Exception("There is the name conflict of graspable objects.");
			}

			SIGVerseLogger.Info("Count of Graspables = " + this.graspables.Count);

			this.playbackRecorder = moderator.playbackManager.GetComponent<IcsPlaybackRecorder>();
		}


		private void Initialize()
		{
			this.taskInfo = ReadTaskInfo();

			if(this.taskInfo.hasImage)
			{
				this.flippedTaskImage = ReadFlippedTaskImage();
			}

			this.targetItem = this.graspables.FirstOrDefault(graspable => graspable.name == this.taskInfo.targetName);

			// Check
			if(this.targetItem==null)
			{
				throw new Exception("The item with that name does not exist. TargetName="+this.taskInfo.targetName);
			}

			this.rosConnections = SIGVerseUtils.FindObjectsOfInterface<IRosConnection>();

			SIGVerseLogger.Info("ROS connection : count=" + this.rosConnections.Length);


			// Set up the voice (Using External executable file)
			this.speechProcess = new System.Diagnostics.Process();
			this.speechProcess.StartInfo.FileName = Application.dataPath + "/" + SpeechExePath;
			this.speechProcess.StartInfo.CreateNoWindow = true;
			this.speechProcess.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

			this.isSpeechUsed = System.IO.File.Exists(this.speechProcess.StartInfo.FileName);

			this.speechInfoQue = new Queue<SpeechInfo>();

			SIGVerseLogger.Info("Text-To-Speech: " + Application.dataPath + "/" + SpeechExePath);

			//this.isPlacementSucceeded = null;
		}


		public GameObject GetTargetItem()
		{ 
			return this.targetItem; 
		}

		public List<GameObject> GetGraspables()
		{
			return this.graspables;
		}


		public IEnumerator LoosenRigidbodyConstraints(Rigidbody rigidbody)
		{
			while(!rigidbody.IsSleeping())
			{
				yield return null;
			}

			rigidbody.constraints = RigidbodyConstraints.None;
		}


		public TaskInfo GetTaskInfo()
		{
			return this.taskInfo;
		}

		public Texture2D GetFlippedTaskImage()
		{
			return this.flippedTaskImage;
		}


		public void ControlSpeech(bool isTaskFinished)
		{
			if(!this.isSpeechUsed){ return; }

			// Cancel current speech that can be canceled when task finished
			try
			{
				if (isTaskFinished && this.latestSpeechInfo!=null && this.latestSpeechInfo.canCancel && !this.speechProcess.HasExited)
				{
					this.speechProcess.Kill();
				}
			}
			catch (Exception)
			{
				SIGVerseLogger.Warn("Couldn't terminate the speech process, but do nothing.");
				// Do nothing even if an error occurs
			}


			if (this.speechInfoQue.Count <= 0){ return; }

			// Return if the current speech is not over
			if (this.latestSpeechInfo!=null && !this.speechProcess.HasExited){ return; }


			SpeechInfo speechInfo = this.speechInfoQue.Dequeue();

			if(isTaskFinished && speechInfo.canCancel){ return; }

			this.latestSpeechInfo = speechInfo;

			string message = this.latestSpeechInfo.message.Replace("_", " "); // Remove "_"

			this.speechProcess.StartInfo.Arguments = "\"" + message + "\" \"Language=" + SpeechLanguage + "; Gender=" + this.latestSpeechInfo.gender + "\"";

			try
			{
				this.speechProcess.Start();

				SIGVerseLogger.Info("Spoke :" + message);
			}
			catch (Exception)
			{
				SIGVerseLogger.Warn("Could not speak :" + message);
			}
		}

		public void StopSpeechForcefully()
		{
			if (!this.isSpeechUsed) { return; }

			this.speechInfoQue.Clear();

			try
			{
				if (!this.speechProcess.HasExited)
				{
					this.speechProcess.Kill();
					SIGVerseLogger.Warn("Terminate the speech process.");
				}
			}
			catch (Exception)
			{
				SIGVerseLogger.Warn("Couldn't terminate the speech process, but do nothing.");
			}
		}


		public void AddSpeechQue(string message, string gender, bool canCancel = false)
		{
			if(!this.isSpeechUsed){ return; }

			if (message.Length > MaxMessageLength)
			{
				message = message.Substring(0, MaxMessageLength);
				SIGVerseLogger.Warn("Length of message is over " + MaxMessageLength.ToString() + " charcters.");
			}

			this.speechInfoQue.Enqueue(new SpeechInfo(message, gender, canCancel));
		}

		public void AddSpeechQueModerator(string message, bool canCancel = false)
		{
			this.AddSpeechQue(message, SpeechGenderModerator, canCancel);
		}

		//public void AddSpeechQueModeratorGood(bool canCancel = false)
		//{
		//	this.AddSpeechQue("Good job", SpeechGenderModerator, canCancel);
		//}

		public void AddSpeechQueModeratorFailed(bool canCancel = false)
		{
			this.AddSpeechQue("That's too bad", SpeechGenderModerator, canCancel);
		}

		public void AddSpeechQueHsr(string message, bool canCancel = false)
		{
			this.AddSpeechQue(message, SpeechGenderHsr, canCancel);
		}

		public bool IsSpeaking()
		{
			return this.speechInfoQue.Count != 0 || (this.latestSpeechInfo!=null && !this.speechProcess.HasExited);
		}

		public void InitializePlayback()
		{
			if(IcsConfig.Instance.info.playbackType == IcsPlaybackCommon.PlaybackTypeRecord)
			{
				this.playbackRecorder.Initialize(IcsConfig.Instance.numberOfTrials);
			}
		}


		public bool IsConnectedToRos()
		{
			foreach(IRosConnection rosConnection in this.rosConnections)
			{
				if(!rosConnection.IsConnected())
				{
					return false;
				}
			}
			return true;
		}

		public IEnumerator ClearRosConnections()
		{
			yield return new WaitForSecondsRealtime (1.5f);

			foreach(IRosConnection rosConnection in this.rosConnections)
			{
				rosConnection.Clear();
			}

			SIGVerseLogger.Info("Clear ROS connections");
		}

		public IEnumerator CloseRosConnections()
		{
			yield return new WaitForSecondsRealtime (1.5f);

			foreach(IRosConnection rosConnection in this.rosConnections)
			{
				rosConnection.Close();
			}

			SIGVerseLogger.Info("Close ROS connections");
		}

		public bool IsPlaybackInitialized()
		{
			if(IcsConfig.Instance.info.playbackType == IcsPlaybackCommon.PlaybackTypeRecord)
			{
				if(!this.playbackRecorder.IsInitialized()) { return false; }
			}

			return true;
		}


		public void StartPlayback()
		{
			if(IcsConfig.Instance.info.playbackType == IcsPlaybackCommon.PlaybackTypeRecord)
			{
				bool isStarted = this.playbackRecorder.Record();

				if(!isStarted) { SIGVerseLogger.Warn("Cannot start the world playback recording"); }
			}
		}

		public void StopPlayback()
		{
			if (IcsConfig.Instance.info.playbackType == IcsPlaybackCommon.PlaybackTypeRecord)
			{
				bool isStopped = this.playbackRecorder.Stop();

				if(!isStopped) { SIGVerseLogger.Warn("Cannot stop the world playback recording"); }
			}
		}

		public bool IsPlaybackFinished()
		{
			if(IcsConfig.Instance.info.playbackType == IcsPlaybackCommon.PlaybackTypeRecord)
			{
				if(!this.playbackRecorder.IsWaiting()) { return false; }
			}

			return true;
		}


		private TaskInfo ReadTaskInfo()
		{
			string filePath = String.Format(Application.dataPath + TaskInfoFileNameFormat, IcsConfig.Instance.numberOfTrials);

			if (File.Exists(filePath))
			{
				// File open
				StreamReader streamReader = new StreamReader(filePath, Encoding.UTF8);

				TaskInfo taskInfo = JsonUtility.FromJson<TaskInfo>(streamReader.ReadToEnd());

				streamReader.Close();

				return taskInfo;
			}
			else
			{
				throw new Exception("Task info file does not exist. filePath=" + filePath);
			}
		}

		private Texture2D ReadFlippedTaskImage()
		{
			string filePath = String.Format(Application.dataPath + TaskImageFileNameFormat, IcsConfig.Instance.numberOfTrials);

			if (File.Exists(filePath))
			{
				try
				{
					Texture2D flippedImageTexture;

					using (System.Drawing.Image originalImage = System.Drawing.Image.FromFile(filePath))
					{
						// Flip upside down
						originalImage.RotateFlip(RotateFlipType.RotateNoneFlipY);

						byte[] flippedImageData;
						using (MemoryStream memoryStream = new MemoryStream())
						{
							originalImage.Save(memoryStream, ImageFormat.Jpeg);
							flippedImageData = memoryStream.ToArray();
						}

						flippedImageTexture = new Texture2D(originalImage.Width, originalImage.Height, TextureFormat.RGB24, false);
						flippedImageTexture.LoadImage(flippedImageData);
					}

					return flippedImageTexture;
				}
				catch(Exception ex)
				{
					SIGVerseLogger.Error("Couldn't read the task image file. filePath="+filePath);
					SIGVerseLogger.Error(ex.StackTrace);
					throw ex;
				}
			}
			else
			{
				throw new Exception("Task image file does not exist. filePath=" + filePath);
			}
		}
	}
}

