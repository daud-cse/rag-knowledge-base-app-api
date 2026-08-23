using UttorAI.Api.Domain;

namespace UttorAI.Api.Data;

/// <summary>Fictional sample content so the retrieval pipeline has something to index on first run.
/// Delete these documents from the admin portal once real content is loaded.</summary>
public static class SampleDocuments
{
    public static readonly (string FileName, string Body, Classification Classification)[] All =
    {
        ("Claims_Guideline.md", ClaimsGuideline, Classification.Internal),
        ("Provider_Manual.md", ProviderManual, Classification.Internal),
        ("Member_Guide.md", MemberGuide, Classification.Public),
        ("Executive_Reimbursement_Policy.md", ExecutivePolicy, Classification.Restricted)
    };

    private const string ClaimsGuideline = """
# Contoso Health Claims Guideline (2026)

## Timely filing
The timely filing limit for a participating provider is 90 days from the date of service.
Non-participating providers must submit within 180 days from the date of service.
Claims received after the timely filing limit are denied with remark code CO-29 and cannot be
appealed unless the provider supplies proof of timely submission.

## Required fields on an 837 professional claim
Every 837P claim must contain the billing provider NPI, the rendering provider NPI, the subscriber
member identifier, the date of service, the place of service code, at least one ICD-10 diagnosis
code, and the CPT or HCPCS procedure code with the appropriate modifiers.
Claims missing the rendering provider NPI are rejected at the clearinghouse and never reach
adjudication.

## Corrected claims
A corrected claim must be submitted with claim frequency code 7 and must reference the original
claim number in loop 2300 REF*F8. Submitting a corrected claim without the original claim number
creates a duplicate and is denied with remark code CO-18.

## Coordination of benefits
When Contoso Health is the secondary payer, the primary payer remittance advice must accompany the
claim. Secondary claims submitted without the primary explanation of benefits are pended for 30 days
and then denied.

## Appeals
A provider may appeal a denied claim within 60 days of the remittance date. Appeals must include the
claim number, the reason for the appeal and any supporting clinical documentation.
The standard appeal decision is issued within 30 calendar days.
""";

    private const string ProviderManual = """
# Contoso Health Provider Manual (2026)

## Credentialing
Providers must complete credentialing before submitting claims. The credentialing cycle takes up to
90 days from receipt of a complete application. Recredentialing occurs every three years.

## Prior authorization
Prior authorization is required for inpatient admissions, advanced imaging such as MRI and CT,
durable medical equipment above 500 dollars, and all out-of-network services.
Emergency services never require prior authorization.
An authorization number must appear in loop 2300 REF*G1 of the claim.

## Reading the 835 remittance
The 835 electronic remittance advice reports the adjudication outcome for each claim line.
Claim adjustment group code CO means contractual obligation and cannot be billed to the member.
Group code PR means patient responsibility and may be billed to the member.
Denial code CO-97 means the benefit is included in the payment for another service.
Denial code PR-1 means the amount was applied to the member deductible.
Denial code CO-45 means the charge exceeds the contracted fee schedule amount.

## Claim status
Providers may check claim status through the provider portal or by submitting a 276 transaction.
Contoso Health responds to a 276 with a 277 status response within one business day.

## Electronic funds transfer
Payment is issued weekly on Wednesday. Providers enrolled in electronic funds transfer receive
payment two business days earlier than providers paid by paper check.
""";

    private const string MemberGuide = """
# Contoso Health Member Guide (2026)

## Your plan basics
The individual annual deductible is 1500 dollars and the family deductible is 3000 dollars.
The individual out-of-pocket maximum is 6500 dollars per plan year and the family out-of-pocket
maximum is 13000 dollars per plan year. Once the out-of-pocket maximum is reached, Contoso Health
pays 100 percent of covered in-network services for the rest of the plan year.

## Copayments
The primary care copayment is 25 dollars per visit. The specialist copayment is 50 dollars per
visit. Urgent care is 75 dollars per visit. The emergency room copayment is 350 dollars and is
waived if the member is admitted.

## Preventive care
In-network preventive care such as annual wellness visits, routine immunisations and recommended
screenings is covered at 100 percent with no deductible and no copayment.

## Pharmacy
The pharmacy benefit uses four tiers. Tier 1 generic drugs cost 10 dollars for a 30 day supply.
Tier 2 preferred brand drugs cost 40 dollars. Tier 3 non-preferred brand drugs cost 80 dollars.
Tier 4 specialty drugs are covered at 30 percent coinsurance up to 250 dollars per fill.
A 90 day mail order supply is available for two copayments instead of three.

## Filing a member claim
If a member pays out of pocket, a reimbursement claim must be filed within 12 months of the date of
service using the member reimbursement form together with an itemised receipt.
""";

    private const string ExecutivePolicy = """
# Executive Reimbursement Policy (Restricted)

This document is classified Restricted. It is indexed into the same knowledge base as the other
sample files, but security trimming means only users whose clearance is Restricted can retrieve it.
Sign in as user@contoso.com, whose clearance is Internal, and this content will not appear in any
answer or citation.

## Executive travel
Executive officers are reimbursed for business class travel on flights longer than six hours.
The nightly lodging cap for executive travel is 450 dollars in metropolitan areas.

## Discretionary approvals
The chief medical officer may approve a discretionary claims payment of up to 25000 dollars without
board review. Amounts above that threshold require written board approval.
""";
}
