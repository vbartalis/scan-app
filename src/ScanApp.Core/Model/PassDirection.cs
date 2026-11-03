namespace ScanApp.Core.Model;

/// <summary>
/// Which way the scanner was pushed, named by where the camera sits relative to the push:
/// <see cref="CameraFront"/> means the camera side leads and the laser line follows; <see cref="CameraBack"/>
/// means the laser line leads and the camera trails. Chosen by the user per pass.
/// </summary>
public enum PassDirection { CameraFront, CameraBack }
