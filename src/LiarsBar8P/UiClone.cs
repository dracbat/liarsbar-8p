using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// A copy of one of the game's own buttons or panels with everything the original does taken out
/// of it, so a control the mod adds looks exactly like the game's and does only what the mod says.
///
/// A plain <c>Instantiate</c> copies the wiring along with the looks, and the wiring is the danger.
/// A button's saved clicks travel with the copy, and <c>RemoveAllListeners</c> leaves them where
/// they are: it only clears the ones added from code. The settings footer has two buttons that look
/// alike and one of them is Restore Defaults, so a copy of the wrong one would wipe a player's
/// settings from a button labelled as something else. Two more components bring behaviour of their
/// own that no click event holds: a <c>LocalizeStringEvent</c> writes its own text back over the
/// mod's whenever the language is set, and a <c>StateSwitcher</c> answers the click itself and
/// moves the menu to another screen.
///
/// So the copy is made under a switched-off holder, where none of its Awake or OnEnable can run.
/// Those two components are destroyed by their IL2CPP type name, because the mod has no reference
/// to the localisation assembly. Every button gets a fresh click event, every event trigger keeps
/// only its sounds, and the copy is refused if anything saved is still on it. Only then is it moved
/// where it was asked for and switched on.
///
/// "Anything saved" means on any component, not only those three: a slider, a scroll bar, an
/// input box, a dropdown or one of the game's own scripts can hold saved calls as well. Those are
/// not cleaned, because nothing the mod copies is meant to keep one, so a copy carrying one is
/// refused. They are found by looking through each component's fields for an event. If this
/// build ever cannot look (the log says so once), only buttons, toggles and event triggers are
/// checked, which is every kind of control the mod copies today.
/// </summary>
internal static class UiClone
{
    /// <summary>Components that carry behaviour of their own, removed from every copy.</summary>
    private static readonly string[] Wired = { "LocalizeStringEvent", "StateSwitcher" };

    private static bool _cannotLook;

    /// <summary>
    /// A clean copy of <paramref name="source"/> under <paramref name="parent"/>, switched on, or
    /// null if it could not be made safely. A button on the copy does <paramref name="onClick"/>
    /// and nothing else. Its position is the original's, in the original's parent: set it after.
    /// </summary>
    internal static GameObject Make(GameObject source, Transform parent, string name, Action onClick = null,
                                    params string[] alsoRemove)
    {
        if (source == null || parent == null) return null;

        var hold = new GameObject("LB8P_Hold");
        hold.SetActive(false);
        try
        {
            var copy = UnityEngine.Object.Instantiate(source, hold.transform, false);
            copy.name = name;

            foreach (var part in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (part == null) continue;
                string type = part.GetIl2CppType().Name;
                if (Array.IndexOf(Wired, type) >= 0 || Array.IndexOf(alsoRemove, type) >= 0)
                    UnityEngine.Object.DestroyImmediate(part);
            }

            // A fresh event, not RemoveAllListeners: the saved clicks live in the event itself.
            foreach (var button in copy.GetComponentsInChildren<Button>(true))
                button.onClick = new Button.ButtonClickedEvent();
            foreach (var toggle in copy.GetComponentsInChildren<Toggle>(true))
                toggle.onValueChanged = new Toggle.ToggleEvent();
            foreach (var trigger in copy.GetComponentsInChildren<EventTrigger>(true))
                KeepSounds(trigger);

            int left = Saved(copy);
            if (left != 0)
            {
                Plugin.Log.LogWarning($"[clone] '{name}' still had {left} saved call(s) after cleaning - not used");
                return null;
            }

            if (onClick != null)
            {
                var button = copy.GetComponent<Button>() ?? copy.GetComponentInChildren<Button>(true);
                if (button != null) button.onClick.AddListener(onClick);
            }

            copy.transform.SetParent(parent, false);
            copy.SetActive(true);
            return copy;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[clone] could not copy '{name}': {e.Message}");
            return null;
        }
        finally
        {
            UnityEngine.Object.Destroy(hold);
        }
    }

    /// <summary>
    /// How many saved calls are left anywhere on <paramref name="go"/> that are not a sound: on its
    /// buttons and toggles, on its event triggers, and in any event any other component holds.
    /// Zero is the only safe answer.
    /// </summary>
    internal static int Saved(GameObject go)
    {
        int left = 0;
        foreach (var button in go.GetComponentsInChildren<Button>(true))
            left += Count(button.onClick);
        foreach (var toggle in go.GetComponentsInChildren<Toggle>(true))
            left += Count(toggle.onValueChanged);
        foreach (var trigger in go.GetComponentsInChildren<EventTrigger>(true))
        {
            if (trigger.triggers == null) continue;
            foreach (var entry in trigger.triggers)
                if (entry != null && entry.callback != null && !OnlySounds(entry.callback)) left++;
        }
        return left + SavedElsewhere(go);
    }

    private const Il2CppSystem.Reflection.BindingFlags Fields =
        Il2CppSystem.Reflection.BindingFlags.Instance | Il2CppSystem.Reflection.BindingFlags.Public |
        Il2CppSystem.Reflection.BindingFlags.NonPublic | Il2CppSystem.Reflection.BindingFlags.DeclaredOnly;

    /// <summary>
    /// Saved calls in the events of every other component: each one's own fields, and those of the
    /// classes it is built on, down to Unity's. Buttons, toggles and event triggers are counted above.
    /// </summary>
    private static int SavedElsewhere(GameObject go)
    {
        if (_cannotLook) return 0;

        string looking = "?";
        try
        {
            int left = 0;
            var events = Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Events.UnityEventBase>();
            foreach (var part in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (part == null || part.TryCast<Button>() != null || part.TryCast<Toggle>() != null ||
                    part.TryCast<EventTrigger>() != null) continue;

                for (var type = part.GetIl2CppType(); type != null; type = type.BaseType)
                {
                    looking = type.Name;
                    if (looking == "MonoBehaviour" || looking == "UIBehaviour") break;

                    foreach (var field in type.GetFields(Fields))
                    {
                        if (field == null || !events.IsAssignableFrom(field.FieldType)) continue;
                        var value = field.GetValue(part);
                        left += Count(value != null ? value.TryCast<UnityEngine.Events.UnityEventBase>() : null);
                    }
                }
            }
            return left;
        }
        catch (Exception e)
        {
            _cannotLook = true;
            Plugin.Log.LogWarning($"[clone] cannot look through a {looking} for saved calls ({e.Message}) - " +
                                  "only buttons, toggles and event triggers are checked from now on");
            return 0;
        }
    }

    /// <summary>
    /// The saved calls on an event, read from its own fields. This build has had the methods that
    /// would report them stripped out, and a call to one fails at run time, not at build time.
    /// </summary>
    private static Il2CppSystem.Collections.Generic.List<UnityEngine.Events.PersistentCall> Calls(UnityEngine.Events.UnityEventBase saved)
    {
        var group = saved != null ? saved.m_PersistentCalls : null;
        return group != null ? group.m_Calls : null;
    }

    private static int Count(UnityEngine.Events.UnityEventBase saved)
    {
        var calls = Calls(saved);
        return calls != null ? calls.Count : 0;
    }

    /// <summary>
    /// The game's buttons play their hover and click sounds from an event trigger, and that is
    /// worth keeping: a copy that stays silent sounds broken. Anything else a trigger does goes.
    /// </summary>
    private static void KeepSounds(EventTrigger trigger)
    {
        if (trigger.triggers == null) return;
        foreach (var entry in trigger.triggers)
            if (entry != null && entry.callback != null && !OnlySounds(entry.callback))
                entry.callback = new EventTrigger.TriggerEvent();
    }

    private static bool OnlySounds(UnityEngine.Events.UnityEventBase saved)
    {
        var calls = Calls(saved);
        if (calls == null) return true;

        foreach (var call in calls)
        {
            if (call == null) continue;
            string method = call.m_MethodName;
            if (method != "Play" && method != "PlayOneShot") return false;

            var target = call.m_Target;
            if (target != null && target.TryCast<AudioSource>() == null) return false;
        }
        return true;
    }
}
