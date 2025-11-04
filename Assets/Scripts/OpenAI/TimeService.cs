using System;
using UnityEngine;

public static class TimeService
{
	public static string GetCurrentTimeString()
	{
		var now = DateTime.Now;
		var time = now.ToString("h:mm tt");
		string period = GetPeriod(now.Hour);
		return $"{time} {period}";
	}

	private static string GetPeriod(int hour)
	{
		// 5-11:59 Morning, 12-16:59 Afternoon, 17-20:59 Evening, 21-4:59 Night
		if (hour >= 5 && hour < 12) return "Morning";
		if (hour >= 12 && hour < 17) return "Afternoon";
		if (hour >= 17 && hour < 21) return "Evening";
		return "Night";
	}

	public static event Action<string> OnReminderDue;

	public static void ScheduleReminderAt(DateTime dueLocal, string message)
	{
		EnsureRunner().StartCoroutine(ReminderCoroutine(dueLocal, message));
	}

	public static void ScheduleReminderIn(TimeSpan delay, string message)
	{
		var due = DateTime.Now.Add(delay);
		ScheduleReminderAt(due, message);
	}

	private static System.Collections.IEnumerator ReminderCoroutine(DateTime due, string message)
	{
		while (DateTime.Now < due)
		{
			yield return null;
		}
		OnReminderDue?.Invoke(message);
	}

	private static TimeServiceRunner runner;
	private static TimeServiceRunner EnsureRunner()
	{
		if (runner == null)
		{
			var go = new GameObject("TimeServiceRunner");
			UnityEngine.Object.DontDestroyOnLoad(go);
			runner = go.AddComponent<TimeServiceRunner>();
		}
		return runner;
	}

	private class TimeServiceRunner : MonoBehaviour { }
}

