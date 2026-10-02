namespace Apocaraider
{
    // Marker: Apocaraider handles melee hits on fitted wheels itself (Tracers.SwingAtFittedWheels, 1.5.1). Apocapatrol looks this type up
    // by name and leaves its own plain version off while Apocaraider is installed.
    public static class MeleeWheels
    {
        public const bool Handled = true;
    }
}
