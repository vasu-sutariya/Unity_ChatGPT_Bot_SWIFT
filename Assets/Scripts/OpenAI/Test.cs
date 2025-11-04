using UnityEngine;
using OpenAI_API;
using System.Net.Http;
using UnityEngine.UI;
using TMPro;
using System;
using System.IO;
using System.Text;
using UnityEngine.Networking;
using System.Text.RegularExpressions;

public class Test : MonoBehaviour
{
	[SerializeField] private string apiKey;

	[SerializeField] private TMP_Text chatText;
	[SerializeField] private bool logToConsole = true;
	[SerializeField] private bool speakResponses = true;
	[SerializeField] private AudioSource ttsSource;
	[SerializeField] private Image speakingBulb;
	[SerializeField] private Color speakingColor = default;
	[SerializeField] private Color idleColor = default;

	[SerializeField] private bool pauseWhileSpeaking = true;
	[SerializeField, Range(0, 2000)] private int postSpeakCooldownMs = 300;
	[SerializeField] private bool autoListen = true;
	[SerializeField, Range(0.001f, 0.1f)] private float vadThreshold = 0.01f;
	[SerializeField, Range(50, 1000)] private int vadWindowMs = 200;
	[SerializeField, Range(200, 2000)] private int vadSilenceMs = 800;
	[SerializeField, Range(200, 3000)] private int vadMinSpeechMs = 400;
	[SerializeField] private int maxRecordSeconds = 30;


	private OpenAIAPI api;
	private OpenAI_API.Chat.Conversation chat;

	private AudioClip recordingClip;
	private string microphoneDevice;
	private const int sampleRate = 16000;
	private bool micRunning;
	private int lastSamplePosition;
	private bool inSpeech;
	private int currentSpeechMs;
	private int currentSilenceMs;
	private System.Collections.Generic.List<float> speechBuffer = new System.Collections.Generic.List<float>(16000);
	private bool isSpeaking;

	private async void Start()
	{

		if (string.IsNullOrWhiteSpace(apiKey))
		{
			Debug.LogError("Set apiKey in the inspector.");
			return;
		}

		api = new OpenAIAPI(apiKey);

		chat = api.Chat.CreateConversation();
		chat.AppendSystemMessage("You are a Patient Care Assistant. If the user asks for the current local time, respond with exactly [TIME_NOW] and nothing else. For reminders, always reply with a single trigger line in one of these formats, and nothing else: [REMIND|IN|1h3m10s|message] or [REMIND|IN|10s|message] or [REMIND|AT|08:34 PM|message].");
		AppendToChat("Ready. Tap Record to speak.");

		if (speakingColor == default) speakingColor = Color.red;
		if (idleColor == default) idleColor = Color.green;
		SetSpeaking(false);

	if (ttsSource == null)
	{
		var existing = GetComponent<AudioSource>();
		ttsSource = existing != null ? existing : gameObject.AddComponent<AudioSource>();
	}

	TimeService.OnReminderDue += HandleReminderDue;

	if (autoListen)
	{
		StartMicLoop();
	}
	}

	private void StartMicLoop()
	{
		if (Microphone.devices.Length == 0)
		{
			AppendToChat("Error: No microphone found.");
			return;
		}
		microphoneDevice = Microphone.devices[0];
		recordingClip = Microphone.Start(microphoneDevice, true, 300, sampleRate);
		micRunning = true;
		lastSamplePosition = 0;
		inSpeech = false;
		currentSpeechMs = 0;
		currentSilenceMs = 0;
		AppendToChat("Listening...");
	}

	private void StopMicLoop()
	{
		if (!micRunning) return;
		Microphone.End(microphoneDevice);
		micRunning = false;
		inSpeech = false;
		speechBuffer.Clear();
	}

	private void OnDisable()
	{
		if (micRunning)
		{
			Microphone.End(microphoneDevice);
			micRunning = false;
		}
	}

	private void Update()
	{
		if (!autoListen || !micRunning || recordingClip == null) return;
		if (pauseWhileSpeaking && (isSpeaking || (ttsSource != null && ttsSource.isPlaying))) return;

		int windowSamples = Mathf.Max(1, sampleRate * vadWindowMs / 1000);
		int pos = Microphone.GetPosition(microphoneDevice);
		if (pos < 0) return;

		int available = pos >= lastSamplePosition ? (pos - lastSamplePosition) : ((recordingClip.samples - lastSamplePosition) + pos);
		while (available >= windowSamples)
		{
			var temp = new float[windowSamples];
			recordingClip.GetData(temp, lastSamplePosition);
			lastSamplePosition += windowSamples;
			if (lastSamplePosition >= recordingClip.samples) lastSamplePosition -= recordingClip.samples;
			available -= windowSamples;

			float rms = 0f;
			for (int i = 0; i < temp.Length; i++) rms += temp[i] * temp[i];
			rms = Mathf.Sqrt(rms / temp.Length);

			int deltaMs = vadWindowMs;
			bool voiced = rms >= vadThreshold;

			if (!inSpeech)
			{
				if (voiced)
				{
					inSpeech = true;
					currentSpeechMs = deltaMs;
					currentSilenceMs = 0;
					speechBuffer.Clear();
					speechBuffer.AddRange(temp);
				}
			}
			else
			{
				speechBuffer.AddRange(temp);
				if (voiced)
				{
					currentSpeechMs += deltaMs;
					currentSilenceMs = 0;
				}
				else
				{
					currentSilenceMs += deltaMs;
					if (currentSilenceMs >= vadSilenceMs)
					{
						if (currentSpeechMs >= vadMinSpeechMs)
						{
							var samples = speechBuffer.ToArray();
							inSpeech = false;
							currentSpeechMs = 0;
							currentSilenceMs = 0;
							_ = SendUtteranceFromSamples(samples);
							speechBuffer.Clear();
						}
						else
						{
							inSpeech = false;
							currentSpeechMs = 0;
							currentSilenceMs = 0;
							speechBuffer.Clear();
						}
					}
				}
			}
		}
	}

	private async System.Threading.Tasks.Task SendUtteranceFromSamples(float[] samples)
	{
		if (samples == null || samples.Length == 0) return;
		var clip = AudioClip.Create("utterance", samples.Length, 1, sampleRate, false);
		clip.SetData(samples, 0);
		var wavBytes = AudioClipToWav(clip);
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
			if (!string.IsNullOrEmpty(response) && response.Contains("[TIME_NOW]"))
			{
				response = TimeService.GetCurrentTimeString();
			}
			else if (TryScheduleReminderFromTrigger(response, out var confirm))
			{
				response = confirm;
			}
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
			if (!string.IsNullOrEmpty(response) && response.Contains("[TIME_NOW]"))
			{
				response = TimeService.GetCurrentTimeString();
			}
			else if (TryScheduleReminderFromTrigger(response, out var confirm2))
			{
				response = confirm2;
			}
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
		if (chatText == null) return;
		chatText.text += (chatText.text.Length > 0 ? "\n" : string.Empty) + line;

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
		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(36 + bytesData.Length);
			writer.Write(Encoding.ASCII.GetBytes("WAVE"));
		writer.Write(Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
		writer.Write((short)1);
			writer.Write((short)clip.channels);
			writer.Write(sampleRate);
			int byteRate = sampleRate * clip.channels * 2;
			writer.Write(byteRate);
			short blockAlign = (short)(clip.channels * 2);
			writer.Write(blockAlign);
		writer.Write((short)16);
			writer.Write(Encoding.ASCII.GetBytes("data"));
			writer.Write(bytesData.Length);
			writer.Write(bytesData);

			writer.Flush();
			return memoryStream.ToArray();
		}
	}

	private async System.Threading.Tasks.Task<string> TranscribeWithWhisper(byte[] wavBytes)
	{
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

		if (bytes[0] == (byte)'{' || bytes[0] == (byte)'<')
		{
			AppendToChat("TTS error body: " + req.downloadHandler.text);
			return;
		}

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
			if (pauseWhileSpeaking)
			{
				StartSpeakingGuard(clip.length);
			}
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

	private void StartSpeakingGuard(float clipSeconds)
	{
		StopAllCoroutines();
		SetSpeaking(true);
		// Fully stop mic capture so phone speaker output is not re-captured
		StopMicLoop();
		StartCoroutine(SpeakingGuardCoroutine(Mathf.Max(0f, clipSeconds)));
	}

	private System.Collections.IEnumerator SpeakingGuardCoroutine(float clipSeconds)
	{
		isSpeaking = true;
		if (clipSeconds > 0f)
		{
			yield return new WaitForSeconds(clipSeconds);
		}
		if (postSpeakCooldownMs > 0)
		{
			yield return new WaitForSeconds(postSpeakCooldownMs / 1000f);
		}
		SetSpeaking(false);
		// Resume mic loop after speaking + cooldown
		if (autoListen)
		{
			StartMicLoop();
		}
	}

	private void SetSpeaking(bool value)
	{
		isSpeaking = value;
		if (speakingBulb != null)
		{
			speakingBulb.color = value ? speakingColor : idleColor;
		}
	}

	private bool TryScheduleReminderFromTrigger(string line, out string confirm)
	{
		confirm = null;
		if (string.IsNullOrEmpty(line)) return false;
		var m = Regex.Match(line.Trim(), @"^\[REMIND\|(IN|AT)\|([^|]+)\|(.+)\]$", RegexOptions.IgnoreCase);
		if (!m.Success) return false;
		var mode = m.Groups[1].Value.ToUpperInvariant();
		var spec = m.Groups[2].Value.Trim();
		var message = m.Groups[3].Value.Trim();
		if (mode == "IN")
		{
			if (!TryParseDuration(spec, out var ts)) return false;
			TimeService.ScheduleReminderIn(ts, message);
			confirm = $"Reminder set in {FormatSpan(ts)}: {message}";
			return true;
		}
		else if (mode == "AT")
		{
			if (!DateTime.TryParse(spec, out var t)) return false;
			var now = DateTime.Now;
			var due = new DateTime(now.Year, now.Month, now.Day, t.Hour, t.Minute, 0);
			if (due <= now) due = due.AddDays(1);
			TimeService.ScheduleReminderAt(due, message);
			confirm = $"Reminder set for {due:hh:mm tt}: {message}";
			return true;
		}
		return false;
	}

	private bool TryParseDuration(string s, out TimeSpan ts)
	{
		ts = TimeSpan.Zero;
		int h = 0, m = 0, sec = 0;
		var match = Regex.Match(s, @"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$", RegexOptions.IgnoreCase);
		if (!match.Success) return false;
		if (match.Groups[1].Success) h = int.Parse(match.Groups[1].Value);
		if (match.Groups[2].Success) m = int.Parse(match.Groups[2].Value);
		if (match.Groups[3].Success) sec = int.Parse(match.Groups[3].Value);
		ts = new TimeSpan(h, m, sec);
		return ts.TotalSeconds > 0;
	}

	private string FormatSpan(TimeSpan ts)
	{
		if (ts.TotalHours >= 1)
		{
			int h = (int)ts.TotalHours;
			int m = ts.Minutes;
			int s = ts.Seconds;
			if (m == 0 && s == 0) return $"{h}h";
			if (s == 0) return $"{h}h {m}m";
			return $"{h}h {m}m {s}s";
		}
		if (ts.TotalMinutes >= 1)
		{
			int m = (int)ts.TotalMinutes;
			int s = ts.Seconds;
			if (s == 0) return $"{m}m";
			return $"{m}m {s}s";
		}
		return $"{(int)ts.TotalSeconds}s";
	}

	private async void HandleReminderDue(string message)
	{
		var line = "Reminder: " + message;
		AppendToChat(line);
		if (speakResponses && !string.IsNullOrWhiteSpace(message))
		{
			await Speak(message);
		}
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