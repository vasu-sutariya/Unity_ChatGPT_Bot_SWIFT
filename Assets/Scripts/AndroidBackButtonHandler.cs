using UnityEngine;
using UnityEngine.InputSystem;

public class AndroidBackButtonHandler : MonoBehaviour
{
	[SerializeField] private bool quitOnBackButton = true;

	private void Update()
	{
		if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
		{
			if (quitOnBackButton)
			{
				Application.Quit();
                Debug.Log("Quiting application");
			}
		}
	}
}

