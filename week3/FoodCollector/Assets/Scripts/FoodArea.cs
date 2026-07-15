using System.Collections.Generic;
using UnityEngine;

// TrainingArea 하위에 음식(고기/당근)을 프리팹으로 지정 개수만큼 생성하고,
// 에피소드 시작 때 전부 스테이지 안 랜덤 위치로 재배치한다.
public class FoodArea : MonoBehaviour
{
    [Header("음식 프리팹")]
    [Tooltip("고기 프리팹 (food 태그)")]
    public GameObject goodFoodPrefab;
    [Tooltip("당근 프리팹 (badFood 태그)")]
    public GameObject badFoodPrefab;

    [Header("초기 개수")]
    public int numGoodFood = 8;
    public int numBadFood = 5;

    readonly List<Collectible> m_Foods = new List<Collectible>();

    void Awake()
    {
        SpawnAll(goodFoodPrefab, numGoodFood);
        SpawnAll(badFoodPrefab, numBadFood);
    }

    // prefab을 count개 생성해 area 하위에 붙이고 즉시 랜덤 배치
    void SpawnAll(GameObject prefab, int count)
    {
        if (prefab == null) return;
        for (int i = 0; i < count; i++)
        {
            var go = Instantiate(prefab, transform);
            if (go.TryGetComponent(out Collectible food))
            {
                m_Foods.Add(food);
                food.Respawn();
            }
        }
    }

    // 모든 음식을 랜덤 위치로 재배치 (에피소드 리셋 때 호출)
    public void ResetFoods()
    {
        foreach (var food in m_Foods)
            food.Respawn();
    }
}
