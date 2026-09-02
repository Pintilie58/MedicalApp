using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Single place where a B2C interpretation credit is taken and given back.
    /// Background interpretations RESERVE the credit at launch (so one credit
    /// cannot pay for three parallel uploads) and refund it when the job fails,
    /// is rejected as non-medical, or is interrupted by an app restart.
    /// </summary>
    public static class CreditLedger
    {
        /// <summary>Takes one credit — bonus first, then paid.</summary>
        public static void ReserveOne(User user)
        {
            if (user.BonusCreditsRemaining > 0)
            {
                user.BonusCreditsConsumed += 1;
            }
            else
            {
                user.CreditConsum += 1;
                user.CreditRest = user.Credite - user.CreditConsum;
            }
        }

        /// <summary>
        /// Gives one credit back — bonus first (mirrors the consumption order),
        /// then paid. Never goes below zero.
        /// </summary>
        public static void RefundOne(User user)
        {
            if (user.BonusCreditsConsumed > 0)
            {
                user.BonusCreditsConsumed -= 1;
            }
            else if (user.CreditConsum > 0)
            {
                user.CreditConsum -= 1;
                user.CreditRest = user.Credite - user.CreditConsum;
            }
        }
    }
}
