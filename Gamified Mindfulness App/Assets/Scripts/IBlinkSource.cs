using UnityEngine;
using System;

public interface IBlinkSource
{
    event Action Blinked;
}
