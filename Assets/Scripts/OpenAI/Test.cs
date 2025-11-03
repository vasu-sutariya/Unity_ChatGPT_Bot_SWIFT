using UnityEngine;
using OpenAI_API;
using System.Net.Http;
using UnityEngine.UI;
using TMPro;
using System;
using System.IO;
using System.Text;
 using UnityEngine.Networking;

public class Test : MonoBehaviour
{
	[SerializeField] private string apiKey; // Set your OpenAI API key in the inspector
	[SerializeField] private TMP_Text chatText; // Assign a TMP_Text in the scene
	[SerializeField] private ScrollRect scrollRect; // Optional: assign to auto-scroll
	[SerializeField] private bool logToConsole = true; // Mirror chat to Unity Console
 	[SerializeField] private bool speakResponses = true; // Play assistant replies
 	[SerializeField] private AudioSource ttsSource; // Assign or auto-created

	private OpenAIAPI api;
	private OpenAI_API.Chat.Conversation chat;

	private AudioClip recordingClip;
	private string microphoneDevice;
	private const int sampleRate = 16000; // Whisper works well with 16kHz
	private const int maxRecordSeconds = 30;

	private async void Start()
	{

		if (string.IsNullOrWhiteSpace(apiKey))
		{
			Debug.LogError("Set apiKey in the inspector.");
			return;
		}

		api = new OpenAIAPI(apiKey);

		chat = api.Chat.CreateConversation();
		chat.AppendSystemMessage("You are a Patient Care Assistant.");
		AppendToChat("Ready. Tap Record to speak.");

	if (ttsSource == null)
	{
		var existing = GetComponent<AudioSource>();
		ttsSource = existing != null ? existing : gameObject.AddComponent<AudioSource>();
	}
	}

	public void StartRecording()
	{
		if (Microphone.devices.Length == 0)
		{
			AppendToChat("Error: No microphone found.");
			return;
		}
		microphoneDevice = Microphone.devices[0];
		recordingClip = Microphone.Start(microphoneDevice, false, maxRecordSeconds, sampleRate);
		AppendToChat("Listening...");
	}

	public async void StopRecordingAndSend()
	{
		if (recordingClip == null)
		{
			AppendToChat("Error: Not recording.");
			return;
		}
		Microphone.End(microphoneDevice);

		var wavBytes = AudioClipToWav(recordingClip);
		recordingClip = null;

		var userText = await TranscribeWithWhisper(wavBytes);
		if (string.IsNullOrWhiteSpace(userText))
		{
			AppendToChat("Error: Transcription failed.");
			return;
		}

		AppendToChat("You: " + userText);
		chat.AppendUserInput(userText);

		try
		{
			var response = await chat.GetResponseFromChatbot();
			AppendToChat("Assistant: " + response);
 			if (speakResponses && !string.IsNullOrWhiteSpace(response))
 			{
 				await Speak(response);
 			}
		}
		catch (HttpRequestException ex)
		{
			AppendToChat("Error: " + ex.Message);
		}
		catch (System.Exception ex)
		{
			AppendToChat("Error: " + ex.Message);
		}
	}

	private void AppendToChat(string line)
	{
		if (chatText != null)
		{
			chatText.text += (chatText.text.Length > 0 ? "\n" : string.Empty) + line;
			if (scrollRect != null)
			{
				Canvas.ForceUpdateCanvases();
				scrollRect.verticalNormalizedPosition = 0f;
			}
		}

		if (logToConsole)
		{
			Debug.Log(line);
		}
	}

	private byte[] AudioClipToWav(AudioClip clip)
	{
		float[] samples = new float[clip.samples * clip.channels];
		clip.GetData(samples, 0);

		short[] intData = new short[samples.Length];
		for (int i = 0; i < samples.Length; i++)
		{
			float f = Mathf.Clamp(samples[i], -1f, 1f);
			intData[i] = (short)Mathf.RoundToInt(f * short.MaxValue);
		}

		byte[] bytesData = new byte[intData.Length * 2];
		Buffer.BlockCopy(intData, 0, bytesData, 0, bytesData.Length);

		using (var memoryStream = new MemoryStream())
		using (var writer = new BinaryWriter(memoryStream, Encoding.UTF8))
		{
			// RIFF header
			writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + bytesData.Length);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));

			// fmt chunk
			writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((short)1); // PCM
			writer.Write((short)clip.channels);
			writer.Write(sampleRate);
			int byteRate = sampleRate * clip.channels * 2;
			writer.Write(byteRate);
			short blockAlign = (short)(clip.channels * 2);
			writer.Write(blockAlign);
			writer.Write((short)16); // bits per sample

			// data chunk
			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(bytesData.Length);
			writer.Write(bytesData);

			writer.Flush();
			return memoryStream.ToArray();
		}
	}

	private async System.Threading.Tasks.Task<string> TranscribeWithWhisper(byte[] wavBytes)
	{
		// Use UnityWebRequest to call Whisper transcription endpoint
		var url = "https://api.openai.com/v1/audio/transcriptions";
		var form = new WWWForm();
		form.AddField("model", "whisper-1");
		form.AddBinaryData("file", wavBytes, "speech.wav", "audio/wav");

		using (var req = UnityEngine.Networking.UnityWebRequest.Post(url, form))
		{
			req.SetRequestHeader("Authorization", "Bearer " + apiKey);
			req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
			var op = req.SendWebRequest();
			while (!op.isDone) await System.Threading.Tasks.Task.Yield();

			if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
			{
				Debug.LogError("Whisper error: " + req.error + "\n" + req.downloadHandler.text);
				return null;
			}

			var json = req.downloadHandler.text;
			var parsed = JsonUtility.FromJson<WhisperResponse>(json);
			return parsed != null ? parsed.text : null;
		}
	}

	[System.Serializable]
	private class WhisperResponse
	{
		public string text;
	}

	private async System.Threading.Tasks.Task Speak(string text)
	{
		if (ttsSource == null) return;

		var url = "https://api.openai.com/v1/audio/speech";
		var payload = "{" +
			"\"model\":\"tts-1\"," +
			"\"voice\":\"alloy\"," +
			"\"response_format\":\"mp3\"," +
			"\"input\":\"" + EscapeForJson(text) + "\"}";

		var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
		var bodyRaw = Encoding.UTF8.GetBytes(payload);
		req.uploadHandler = new UploadHandlerRaw(bodyRaw);
		req.downloadHandler = new DownloadHandlerBuffer();
		req.SetRequestHeader("Authorization", "Bearer " + apiKey);
		req.SetRequestHeader("Content-Type", "application/json");
		req.SetRequestHeader("Accept", "audio/mpeg");

		var op = req.SendWebRequest();
		while (!op.isDone) await System.Threading.Tasks.Task.Yield();

		if (req.result != UnityWebRequest.Result.Success)
		{
			AppendToChat("TTS error: " + req.error + "\n" + req.downloadHandler.text);
			return;
		}

		var bytes = req.downloadHandler.data;
		if (bytes == null || bytes.Length < 4)
		{
			AppendToChat("TTS error: empty/invalid audio data");
			return;
		}

		// Surface JSON error bodies
		if (bytes[0] == (byte)'{' || bytes[0] == (byte)'<')
		{
			AppendToChat("TTS error body: " + req.downloadHandler.text);
			return;
		}

		// Save MP3 to file and load via Unity's decoder
		var mp3Path = Path.Combine(Application.persistentDataPath, "tts.mp3");
		File.WriteAllBytes(mp3Path, bytes);

		using (var getClip = UnityWebRequestMultimedia.GetAudioClip("file://" + mp3Path, AudioType.MPEG))
		{
			var clipOp = getClip.SendWebRequest();
			while (!clipOp.isDone) await System.Threading.Tasks.Task.Yield();

			if (getClip.result != UnityWebRequest.Result.Success)
			{
				AppendToChat("Audio load error: " + getClip.error);
				return;
			}

			var clip = DownloadHandlerAudioClip.GetContent(getClip);
			ttsSource.clip = clip;
			ttsSource.Play();
		}
	}

	private static string EscapeForJson(string s)
	{
		if (string.IsNullOrEmpty(s)) return string.Empty;
		s = s.Replace("\\", "\\\\");
		s = s.Replace("\"", "\\\"");
		s = s.Replace("\n", "\\n");
		s = s.Replace("\r", "\\r");
		s = s.Replace("\t", "\\t");
		return s;
	}

	private AudioClip WavToAudioClip(byte[] wav)
	{
		try
		{
			using (var stream = new MemoryStream(wav))
			using (var reader = new BinaryReader(stream))
			{
				var riff = new string(reader.ReadChars(4));
				reader.ReadInt32();
				var wave = new string(reader.ReadChars(4));
				if (riff != "RIFF" || wave != "WAVE") return null;

				short channels = 1;
				int hz = 16000;
				short bits = 16;
				short audioFormat = 1; // 1 = PCM, 3 = IEEE float
				byte[] pcmData = null;

				while (reader.BaseStream.Position < reader.BaseStream.Length)
				{
					var chunkId = new string(reader.ReadChars(4));
					int chunkSize = reader.ReadInt32();
					if (chunkId == "fmt ")
					{
						audioFormat = reader.ReadInt16();
						channels = reader.ReadInt16();
						hz = reader.ReadInt32();
						reader.ReadInt32(); // byteRate
						reader.ReadInt16(); // blockAlign
						bits = reader.ReadInt16();
						int remaining = chunkSize - 16;
						if (remaining > 0) reader.ReadBytes(remaining);
					}
					else if (chunkId == "data")
					{
						pcmData = reader.ReadBytes(chunkSize);
						break;
					}
					else
					{
						reader.ReadBytes(chunkSize);
					}
				}

				if (pcmData == null) return null;
				int bytesPerSample = bits / 8;
				if (bytesPerSample <= 0) return null;
				int sampleCount = pcmData.Length / bytesPerSample;
				int sampleFrames = sampleCount / channels;
				float[] samples = new float[sampleFrames * channels];

				if (audioFormat == 1 && bits == 16)
				{
					for (int i = 0, s = 0; i + 1 < pcmData.Length; i += 2, s++)
					{
						short val = (short)(pcmData[i] | (pcmData[i + 1] << 8));
						samples[s] = val / 32768f;
					}
				}
				else if (audioFormat == 1 && bits == 8)
				{
					for (int i = 0; i < pcmData.Length; i++) samples[i] = (pcmData[i] - 128) / 128f;
				}
				else if (audioFormat == 3 && bits == 32)
				{
					for (int i = 0, s = 0; i + 3 < pcmData.Length; i += 4, s++)
					{
						float f = BitConverter.ToSingle(pcmData, i);
						samples[s] = Mathf.Clamp(f, -1f, 1f);
					}
				}
				else
				{
					return null;
				}

				var clip = AudioClip.Create("tts", sampleFrames, channels, hz, false);
				clip.SetData(samples, 0);
				return clip;
			}
		}
		catch
		{
			return null;
		}
	}
}